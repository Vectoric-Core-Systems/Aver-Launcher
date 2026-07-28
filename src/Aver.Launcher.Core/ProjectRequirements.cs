using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Aver.Launcher.Core;

/// <summary>One reason a project needs a module.</summary>
/// <param name="Module">The <c>AVER_MODULE_*</c> key required.</param>
/// <param name="Reason">What in the project showed it, phrased for a user to act on.</param>
public sealed record RequirementEvidence(string Module, string Reason)
{
    /// <summary>Display name of the module, e.g. "Physics".</summary>
    public string ModuleName => EngineOptions.Find(Module)?.DisplayName ?? Module;
}

/// <summary>
/// What an engine edition must contain for a project to actually work in it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the version floor is not a capability check. <c>ENGINE Aver 0.1.0</c> is
/// satisfied by every 0.1.0 build, including one compiled without physics -- so SkyForge opens in the
/// Minimal edition, renders its level perfectly, and then does nothing when you press Play: the
/// character cannot move and the crates do not fall. Nothing errors. That is the failure this class
/// is for.
/// </para>
/// <para>
/// <c>.ocproject</c> cannot declare required modules -- there is no such key, and unknown keys are
/// ignored by design -- so the requirements are INFERRED from what the project contains. Three
/// sources, in descending order of authority:
/// </para>
/// <list type="number">
///   <item><description>
///     The compiled <c>Binaries\Scripts\Scripts.dll</c>. Its TypeReference table names exactly the
///     engine types the code binds to, which is a fact rather than a guess.
///   </description></item>
///   <item><description>
///     The C# sources, scanned for the same type names, when nothing has been compiled yet. This one
///     IS a heuristic: it can be fooled by a name in a comment or a string.
///   </description></item>
///   <item><description>
///     Content on disk -- a level file needs the scene, materials and textures need PBR.
///   </description></item>
/// </list>
/// <para>
/// The bias is deliberate: over-reporting a requirement costs the user a warning they can ignore,
/// while under-reporting costs them a Play button that silently does nothing.
/// </para>
/// </remarks>
public sealed class ProjectRequirements
{
    private readonly Dictionary<string, RequirementEvidence> _byModule = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Required <c>AVER_MODULE_*</c> keys.</summary>
    public IReadOnlyCollection<string> Modules => _byModule.Keys;

    /// <summary>One piece of evidence per required module -- the first and clearest found.</summary>
    public IReadOnlyList<RequirementEvidence> Evidence => _byModule.Values.ToList();

    /// <summary>True when nothing could be determined (an empty or unreadable project).</summary>
    public bool IsEmpty => _byModule.Count == 0;

    private void Add(string module, string reason)
    {
        // First evidence wins: it is the most authoritative source that ran, and one clear reason
        // beats a list the user has to read through.
        _byModule.TryAdd(module, new RequirementEvidence(module, reason));
    }

    /// <summary>
    /// Types whose use means the project needs the physics module.
    /// </summary>
    /// <remarks>
    /// From <c>scripting/csharp/Aver.Framework/Physics.cs</c> plus <c>AverCharacter</c>, which is a
    /// Jolt <c>CharacterVirtual</c> capsule and therefore dead without physics even though it lives in
    /// Character.cs. The C# API surface is identical in every edition -- the same assemblies ship
    /// regardless -- so the presence of these types in a build proves nothing; only their USE does.
    /// </remarks>
    private static readonly string[] PhysicsTypes =
        ["Physics", "Body", "ContactEvent", "OverlapEvent", "RaycastHit", "AverCharacter"];

    /// <summary>Infers requirements for a parsed project.</summary>
    public static ProjectRequirements Infer(ProjectDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        var req = new ProjectRequirements();

        string content = desc.ContentDir;
        string binaries = desc.BinariesDir;

        // ---- 1. the compiled assembly, if there is one ----
        bool readAssembly = false;
        string scriptsDll = Path.Combine(binaries, "Scripts", "Scripts.dll");
        if (File.Exists(scriptsDll))
        {
            readAssembly = TryReadAssembly(scriptsDll, req);
        }

        // ---- 2. source scan, only when nothing compiled could be read ----
        if (!readAssembly && Directory.Exists(desc.ScriptsDir))
        {
            ScanSources(desc.ScriptsDir, req);
        }

        // ---- 3. content on disk ----
        ScanContent(content, req);

        return req;
    }

    private static bool TryReadAssembly(string dll, ProjectRequirements req)
    {
        try
        {
            using FileStream fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return false;

            MetadataReader md = pe.GetMetadataReader();
            string file = Path.GetFileName(dll);
            bool any = false;

            foreach (TypeReferenceHandle h in md.TypeReferences)
            {
                TypeReference tr = md.GetTypeReference(h);
                string ns = md.GetString(tr.Namespace);
                string name = md.GetString(tr.Name);
                any = true;

                switch (ns)
                {
                    case "Aver.Framework":
                        if (Array.IndexOf(PhysicsTypes, name) >= 0)
                        {
                            req.Add(EngineOptions.Physics, $"{file} binds to Aver.Framework.{name}");
                        }
                        req.Add(EngineOptions.Framework, $"{file} binds to Aver.Framework.{name}");
                        break;
                    case "Aver.Scene":
                        req.Add(EngineOptions.Scene, $"{file} binds to Aver.Scene.{name}");
                        break;
                    case "Aver.Scripting":
                        req.Add(EngineOptions.Scripting, $"{file} binds to Aver.Scripting.{name}");
                        break;
                    case "Aver.Materials":
                        req.Add(EngineOptions.Pbr, $"{file} binds to Aver.Materials.{name}");
                        break;
                    default:
                        break;
                }
            }

            // A compiled assembly implies a scripting host regardless of what it references.
            if (any) req.Add(EngineOptions.Scripting, $"{file} is a compiled gameplay assembly");
            return any;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // An unreadable assembly falls through to the source scan rather than failing the project.
            return false;
        }
    }

    private static void ScanSources(string scriptsDir, ProjectRequirements req)
    {
        string[] files;
        try { files = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (string f in files)
        {
            // Build output under Content\Scripts is a copy of the engine's own assemblies' sources in
            // some layouts; it is not the project's code.
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text;
            try { text = File.ReadAllText(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            string name = Path.GetFileName(f);
            req.Add(EngineOptions.Scripting, $"{name} is a gameplay script");

            if (text.Contains("using Aver.Framework", StringComparison.Ordinal))
            {
                req.Add(EngineOptions.Framework, $"{name} uses Aver.Framework");
            }
            if (text.Contains("using Aver.Scene", StringComparison.Ordinal))
            {
                req.Add(EngineOptions.Scene, $"{name} uses Aver.Scene");
            }

            foreach (string t in PhysicsTypes)
            {
                if (ContainsToken(text, t))
                {
                    req.Add(EngineOptions.Physics, $"{name} references {t}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whole-identifier match, so <c>Body</c> does not fire on <c>BodyText</c> or <c>RigidBody</c>.
    /// </summary>
    private static bool ContainsToken(string text, string token)
    {
        int i = 0;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !IsIdent(text[i - 1]);
            int end = i + token.Length;
            bool rightOk = end >= text.Length || !IsIdent(text[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;

        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static void ScanContent(string contentDir, ProjectRequirements req)
    {
        if (!Directory.Exists(contentDir)) return;

        try
        {
            foreach (string f in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                switch (Path.GetExtension(f).ToLowerInvariant())
                {
                    case ".ocworld":
                    case ".ocmap":
                        req.Add(EngineOptions.Scene, $"{name} is a level, which the scene module loads");
                        break;
                    case ".ocmat":
                        req.Add(EngineOptions.Pbr, $"{name} is a material");
                        break;
                    case ".octex":
                    case ".png":
                    case ".jpg":
                        req.Add(EngineOptions.Pbr, $"{name} is a texture, which materials sample");
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially readable Content tree still yields whatever was seen.
        }
    }

    /// <summary>Requirements this edition does not satisfy.</summary>
    public IReadOnlyList<RequirementEvidence> MissingIn(EngineEdition edition)
    {
        ArgumentNullException.ThrowIfNull(edition);
        return _byModule.Values.Where(e => !edition.IsOn(e.Module)).ToList();
    }

    /// <summary>True when this edition contains everything the project needs.</summary>
    public bool SatisfiedBy(EngineEdition edition) => MissingIn(edition).Count == 0;
}
