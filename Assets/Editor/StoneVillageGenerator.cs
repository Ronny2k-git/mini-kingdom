// StoneVillageGenerator.cs
// Editor tool that builds a procedural "Stone Village": an irregular cobblestone plaza, curved
// stone streets, dirt lanes to every house, randomised houses with yards, a downtown dressed with
// KayKit RPG Tools, a stream with a bridge, and nature around it.
//
// Open it from: Tools > Mini Kingdom > Stone Village
//
// JS/TS notes:
// - Everything in "Assets/Editor" only runs inside the Unity Editor (never in a build), a bit like
//   a devDependency / build script.
// - System.Random(seed) is a seeded PRNG: same seed => same numbers => same village.
//   UnityEngine.Random is global state, so we never use it here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// ---------------------------------------------------------------------------------------------
// The window: only UI. All the work happens in StoneVillageBuilder below.
// ---------------------------------------------------------------------------------------------
public class StoneVillageGenerator : EditorWindow
{
    // [SerializeField] makes Unity remember these values across script recompiles.
    [SerializeField] int seed = 1234;
    [SerializeField] float villageRadius = 55f;
    [SerializeField] int houseCount = 30;
    [SerializeField] float downtownRadius = 12f;
    [SerializeField] int mainStreetCount = 4;

    [MenuItem("Tools/Mini Kingdom/Stone Village")]
    static void Open() => GetWindow<StoneVillageGenerator>("Stone Village");

    // OnGUI is called every time the window repaints (like a React render function).
    void OnGUI()
    {
        EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            seed = EditorGUILayout.IntField("Seed", seed);
            if (GUILayout.Button("Random", GUILayout.Width(70))) seed = new System.Random().Next(1, 999999);
        }
        villageRadius = EditorGUILayout.Slider("Village Radius", villageRadius, 30f, 90f);
        houseCount = EditorGUILayout.IntSlider("House Count", houseCount, 8, 60);
        downtownRadius = EditorGUILayout.Slider("Downtown Radius", downtownRadius, 8f, 20f);
        mainStreetCount = EditorGUILayout.IntSlider("Main Streets", mainStreetCount, 3, 5);

        EditorGUILayout.Space();
        if (GUILayout.Button("1. Create / Open Stone Village Scene"))
            StoneVillageBuilder.OpenOrCreateScene();
        if (GUILayout.Button("2. Generate", GUILayout.Height(32)))
            new StoneVillageBuilder(seed, villageRadius, houseCount, downtownRadius, mainStreetCount).Generate();
        if (GUILayout.Button("3. Save as Prefab"))
            StoneVillageBuilder.SaveAsPrefab();

        EditorGUILayout.Space();
        if (GUILayout.Button("Print Model Sizes"))
            StoneVillageBuilder.PrintModelSizes();
    }
}

// ---------------------------------------------------------------------------------------------
// The builder: one instance per "Generate" click. Holds the random generator and everything the
// build steps share (streets, occupied areas...).
// ---------------------------------------------------------------------------------------------
public class StoneVillageBuilder
{
    // ---------- paths ----------
    const string HexRoot = "Assets/Models/KayKit_Medieval_Hexagon_Pack_1.0_FREE/Assets/fbx(unity)";
    const string ToolsRoot = "Assets/Models/KayKit_RPGTools/fbx(unity)";
    const string OldScenePath = "Assets/Scenes/MedievalVillage.unity";
    const string ScenePath = "Assets/Scenes/StoneVillage.unity";
    const string OldVillagePrefab = "Assets/Prefabs/Village.prefab";
    const string PrefabPath = "Assets/Prefabs/StoneVillage.prefab";
    const string MatFolder = "Assets/Materials/StoneVillage";
    const string TexFolder = "Assets/Textures";
    const string GenFolder = "Assets/Generated/StoneVillage";
    const string ProfilePath = "Assets/Settings/StoneVillageVolumeProfile.asset";
    const string RootName = "StoneVillage";

    // Every flat layer gets its own height so overlapping layers never flicker (z-fighting).
    const float YPatchGrass = 0.01f, YPatchDirt = 0.02f, YBank = 0.02f, YLane = 0.03f,
                YWater = 0.04f, YStone = 0.05f, YPlaza = 0.06f, YCurb = 0.075f;

    static readonly Color[] TintPalette =
    {
        new Color(1f, 0.97f, 0.92f),   // warm white
        new Color(1f, 0.90f, 0.74f),   // ochre
        new Color(0.90f, 0.90f, 0.93f),// light grey
        new Color(1f, 0.92f, 0.86f),   // soft rose
        new Color(0.95f, 0.88f, 0.78f),// sand
        Color.white,                   // original colours
    };

    // ---------- settings ----------
    readonly System.Random rng;
    readonly int seed;
    readonly float R;            // village radius
    readonly int houseCount;
    readonly float downR;        // downtown (plaza) radius
    readonly int streetCount;

    // ---------- shared data ----------
    // C# note: a 'class' is a reference type (like a JS object: assigning it copies the reference).
    // A 'struct' (like Circle below) is a value type: assigning it copies the whole value, like a number.

    /// A polyline with a width per point. Used for streets, lanes, roads and the stream.
    class Track
    {
        public List<Vector3> pts = new List<Vector3>();
        public List<float> widths = new List<float>();
        public string kind;
    }

    /// An area that is already taken (a house, a stall...). 'owner' lets a house ignore its own area.
    struct Circle
    {
        public Vector3 c; public float r; public int owner;
        public Circle(Vector3 c, float r, int owner) { this.c = c; this.r = r; this.owner = owner; }
    }

    /// A placed building and its local axes, so we can put things "in front" or "to the right" of it.
    class Placed
    {
        public GameObject go; public Vector3 pos, fwd, right; public float w, d, h; public int id;
    }

    Vector3 plazaCenter;
    List<Vector3> plazaRing = new List<Vector3>();
    readonly float[] plazaRadiusByDeg = new float[360];
    readonly List<float> streetAngles = new List<float>();   // degrees, sorted
    readonly List<Track> mainStreets = new List<Track>();
    readonly List<Track> stone = new List<Track>();           // main streets + forks + ring
    readonly List<Track> lanes = new List<Track>();           // dirt lanes to houses
    readonly List<Track> roads = new List<Track>();           // dirt country roads leaving the village
    readonly List<Track> water = new List<Track>();
    readonly List<Vector3> bridges = new List<Vector3>();
    readonly List<Circle> occupied = new List<Circle>();
    Placed tavern, market, blacksmith, church;
    int nextOwner = 1, houseIndex, lightCount;

    Transform root, gGround, gStone, gDirt, gDowntown, gHouses, gProps, gLights, gNature;
    Material matCobble, matCurb, matDirt, matGrass, matGrassDark, matWater, matWood;

    // Mesh builders collect geometry; each becomes ONE mesh at the end (fewer objects = faster).
    readonly MeshBuilder plazaMB = new MeshBuilder(2.5f), stoneMB = new MeshBuilder(2.5f),
        curbMB = new MeshBuilder(1f), laneMB = new MeshBuilder(4f), roadMB = new MeshBuilder(4f),
        grassDarkMB = new MeshBuilder(6f), dirtPatchMB = new MeshBuilder(4f),
        bankMB = new MeshBuilder(4f), waterMB = new MeshBuilder(6f);

    public StoneVillageBuilder(int seed, float radius, int houses, float downtownRadius, int streets)
    {
        this.seed = seed;
        rng = new System.Random(seed);
        R = radius;
        houseCount = houses;
        downR = downtownRadius;
        streetCount = streets;
    }

    // =========================================================================================
    // Entry points
    // =========================================================================================

    public void Generate()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path == OldScenePath)
        {
            EditorUtility.DisplayDialog("Stone Village",
                "This is the MedievalVillage scene. Click \"Create / Open Stone Village Scene\" first, " +
                "so the old village stays untouched.", "OK");
            return;
        }

        try
        {
            Progress("Preparing", 0.02f);
            DeleteOldRoot();
            PrepareFolders();
            CreateMaterials();
            PrintModelSizes();
            CreateHierarchy();

            Progress("Ground", 0.05f); BuildGround();
            Progress("Plaza", 0.08f); BuildPlaza();
            Progress("Streets", 0.12f); BuildStreets();
            Progress("Roads & stream", 0.18f); BuildRoadsAndStream();
            Progress("Downtown", 0.25f); BuildDowntown();
            Progress("Houses", 0.35f); BuildHouses();
            Progress("Curbs", 0.55f); BuildCurbs();
            Progress("Downtown props", 0.6f); BuildDowntownProps();
            Progress("Street lanterns", 0.7f); BuildStreetLanterns();
            Progress("Builder corner", 0.72f); BuildBuilderCorner();
            Progress("Nature", 0.75f); BuildNature();
            Progress("Meshes", 0.9f); BuildMeshObjects();
            Progress("Lighting", 0.95f); SetupLighting(); PlacePlayer();
            MarkStatic();
            LogSummary();

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root.gameObject;
        }
        finally
        {
            // 'finally' always runs, even if something throws — like JS try/finally.
            EditorUtility.ClearProgressBar();
        }
    }

    public static void OpenOrCreateScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
        {
            EditorSceneManager.OpenScene(ScenePath);
            return;
        }

        // Copy the old scene to keep its player, cameras, light and volume, then strip the old village.
        AssetDatabase.CopyAsset(OldScenePath, ScenePath);
        var s = EditorSceneManager.OpenScene(ScenePath);
        foreach (var go in s.GetRootGameObjects())
        {
            bool isOldVillage = PrefabUtility.IsAnyPrefabInstanceRoot(go) &&
                                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == OldVillagePrefab;
            if (isOldVillage || go.name == "Ground") Object.DestroyImmediate(go);
        }
        EditorSceneManager.SaveScene(s);
        Debug.Log($"[StoneVillage] Created {ScenePath}");
    }

    public static void SaveAsPrefab()
    {
        var rootGo = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
        if (rootGo == null)
        {
            EditorUtility.DisplayDialog("Stone Village", "Generate the village first.", "OK");
            return;
        }
        // SaveAsPrefabAssetAndConnect: writes the .prefab and turns the scene object into an instance of it.
        PrefabUtility.SaveAsPrefabAssetAndConnect(rootGo, PrefabPath, InteractionMode.UserAction);
        Debug.Log($"[StoneVillage] Saved {PrefabPath}");
    }

    public static void PrintModelSizes()
    {
        Model("barrel"); // fills the cache
        var sb = new System.Text.StringBuilder("[StoneVillage] Model sizes at scale 1 (meters, X x Y x Z):\n");
        foreach (var name in modelCache.Keys.OrderBy(n => n))
        {
            var s = Native(name).size;
            sb.AppendLine($"  {name,-32} {s.x,6:F2} x {s.y,6:F2} x {s.z,6:F2}");
        }
        Debug.Log(sb.ToString());
    }

    // =========================================================================================
    // Setup
    // =========================================================================================

    void Progress(string step, float t) => EditorUtility.DisplayProgressBar("Stone Village", step, t);

    void DeleteOldRoot()
    {
        foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
            if (go.name == RootName) Object.DestroyImmediate(go);
    }

    string SeedFolder => $"{GenFolder}/Seed_{seed}";

    void PrepareFolders()
    {
        EnsureFolder(MatFolder);
        EnsureFolder(MatFolder + "/Tints");
        EnsureFolder(TexFolder);
        EnsureFolder(GenFolder);
        // Generated meshes are saved per seed. Regenerating the same seed replaces them.
        if (AssetDatabase.IsValidFolder(SeedFolder)) AssetDatabase.DeleteAsset(SeedFolder);
        EnsureFolder(SeedFolder);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    void CreateHierarchy()
    {
        root = new GameObject(RootName).transform;
        gGround = Group("Ground", root);
        var streets = Group("Streets", root);
        gStone = Group("Stone", streets);
        gDirt = Group("Dirt", streets);
        gDowntown = Group("Downtown", root);
        gHouses = Group("Houses", root);
        gProps = Group("Props", root);
        gLights = Group("Lights", root);
        gNature = Group("Nature", root);
    }

    static Transform Group(string name, Transform parent)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    // =========================================================================================
    // Materials & the cobblestone texture
    // =========================================================================================

    void CreateMaterials()
    {
        string colorPath = TexFolder + "/Cobblestone.png";
        string normalPath = TexFolder + "/Cobblestone_Normal.png";
        if (!File.Exists(colorPath) || !File.Exists(normalPath)) GenerateCobblestone(colorPath, normalPath);
        var cobbleTex = AssetDatabase.LoadAssetAtPath<Texture2D>(colorPath);
        var cobbleNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);

        matCobble = MakeMat("Cobblestone", Color.white, 0.2f, cobbleTex, cobbleNormal);
        matCurb = MakeMat("Stone Curb", new Color(0.36f, 0.35f, 0.37f), 0.15f);
        matGrass = MakeMat("Ground Grass Deep", new Color(0.56f, 0.68f, 0.22f), 0.05f);
        matGrassDark = MakeMat("Grass Dark", new Color(0.44f, 0.56f, 0.19f), 0.05f);
        matWater = MakeMat("Stream Water", new Color(0.24f, 0.52f, 0.72f), 0.9f);
        matWood = MakeMat("Wood Post", new Color(0.36f, 0.24f, 0.15f), 0.1f);
        matDirt = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Path Dirt.mat")
                  ?? MakeMat("Dirt", new Color(0.87f, 0.72f, 0.53f), 0.05f);
    }

    static Material MakeMat(string name, Color color, float smoothness, Texture2D tex = null, Texture2D normal = null)
    {
        string path = $"{MatFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        if (tex != null) m.SetTexture("_BaseMap", tex);
        if (normal != null)
        {
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 0.8f);
            m.EnableKeyword("_NORMALMAP");
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    /// Voronoi ("cell") noise: scatter one random point per grid cell; each pixel belongs to the nearest
    /// point (= one stone). Where the nearest and second-nearest points are almost equally far, we're on
    /// the border between two stones => mortar. The texture wraps around, so it tiles seamlessly.
    static void GenerateCobblestone(string colorPath, string normalPath)
    {
        const int size = 512, cells = 7;
        var prng = new System.Random(777); // fixed: the texture doesn't depend on the village seed
        var points = new Vector2[cells, cells];
        var tone = new float[cells, cells];
        var warm = new float[cells, cells];
        for (int x = 0; x < cells; x++)
        for (int y = 0; y < cells; y++)
        {
            points[x, y] = new Vector2(x + 0.15f + 0.7f * (float)prng.NextDouble(),
                                       y + 0.15f + 0.7f * (float)prng.NextDouble());
            tone[x, y] = 0.82f + 0.32f * (float)prng.NextDouble();
            warm[x, y] = (float)prng.NextDouble() - 0.5f;
        }

        var colors = new Color32[size * size];
        var height = new float[size * size];
        var mortarColor = new Color(0.27f, 0.26f, 0.24f);

        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            Vector2 p = new Vector2((px + 0.5f) / size * cells, (py + 0.5f) / size * cells);
            int cx = Mathf.FloorToInt(p.x), cy = Mathf.FloorToInt(p.y);
            float f1 = float.MaxValue, f2 = float.MaxValue;
            int ox = 0, oy = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int ix = cx + dx, iy = cy + dy;
                int wx = (ix % cells + cells) % cells, wy = (iy % cells + cells) % cells; // wrap
                Vector2 fp = points[wx, wy] + new Vector2(ix - wx, iy - wy);
                float d = (p - fp).magnitude;
                if (d < f1) { f2 = f1; f1 = d; ox = wx; oy = wy; }
                else if (d < f2) f2 = d;
            }

            float edge = f2 - f1;
            float stoneMask = Mathf.InverseLerp(0.05f, 0.11f, edge);       // 0 = mortar, 1 = stone
            float bevel = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.4f, edge));
            float g = 0.56f * tone[ox, oy];
            var stoneColor = new Color(g * (1f + 0.07f * warm[ox, oy]), g, g * (1f - 0.07f * warm[ox, oy]));
            stoneColor *= Mathf.Lerp(0.72f, 1f, bevel);
            float noise = ((float)prng.NextDouble() - 0.5f) * 0.06f;
            Color c = Color.Lerp(mortarColor, stoneColor, stoneMask) + new Color(noise, noise, noise);
            c.a = 1f;
            colors[py * size + px] = c;
            height[py * size + px] = stoneMask * (0.4f + 0.6f * bevel);
        }

        // Normal map from the height: the slope in X and Y tells which way each pixel "faces".
        var normals = new Color32[size * size];
        const float strength = 3f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float hl = height[y * size + (x - 1 + size) % size], hr = height[y * size + (x + 1) % size];
            float hd = height[((y - 1 + size) % size) * size + x], hu = height[((y + 1) % size) * size + x];
            var n = new Vector3((hl - hr) * strength, (hd - hu) * strength, 1f).normalized;
            normals[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
        }

        WritePng(colorPath, colors, size);
        WritePng(normalPath, normals, size);
        AssetDatabase.ImportAsset(colorPath);
        AssetDatabase.ImportAsset(normalPath);
        ConfigureTexture(colorPath, false);
        ConfigureTexture(normalPath, true);
    }

    static void WritePng(string path, Color32[] pixels, int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    static void ConfigureTexture(string path, bool isNormal)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.anisoLevel = 4;
        importer.SaveAndReimport();
    }

    // =========================================================================================
    // Ground, plaza, streets
    // =========================================================================================

    void BuildGround()
    {
        // Unity's Plane primitive is 10x10 m, so scale = size / 10.
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.SetParent(gGround, false);
        float size = R * 4.2f;
        ground.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);
        ground.GetComponent<Renderer>().sharedMaterial = matGrass;
    }

    void BuildPlaza()
    {
        // Slightly off-center from the village origin.
        plazaCenter = Dir(Range(0f, Mathf.PI * 2f)) * Range(0f, downR * 0.25f);

        // 7–10 corners with random radius, then smoothed with a closed spline => organic blob.
        int n = rng.Next(7, 11);
        var corners = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float a = (i + Range(-0.3f, 0.3f)) / n * Mathf.PI * 2f;
            corners.Add(plazaCenter + Dir(a) * downR * Range(0.75f, 1.25f));
        }
        plazaRing = SampleSpline(corners, 1.2f, true);

        // Pre-compute the plaza radius for every degree (ray from the center hitting the outline).
        for (int deg = 0; deg < 360; deg++)
        {
            var d = Dir(deg * Mathf.Deg2Rad);
            float best = downR;
            for (int i = 0; i < plazaRing.Count; i++)
            {
                var a = plazaRing[i] - plazaCenter;
                var b = plazaRing[(i + 1) % plazaRing.Count] - plazaCenter;
                if (RaySegment(new Vector2(d.x, d.z), new Vector2(a.x, a.z), new Vector2(b.x, b.z), out float t))
                {
                    best = t;
                    break;
                }
            }
            plazaRadiusByDeg[deg] = best;
        }

        plazaMB.AddFan(plazaCenter, plazaRing, YPlaza);

        // A darker, trampled band of grass around the plaza so the edge blends in.
        var band = plazaRing.Select(p => plazaCenter + Flat(p - plazaCenter).normalized *
                                         (Flat(p - plazaCenter).magnitude + Range(1.8f, 3f))).ToList();
        grassDarkMB.AddFan(plazaCenter, band, YPatchGrass);
    }

    float PlazaRadius(float angleRad)
    {
        float deg = Mathf.Repeat(angleRad * Mathf.Rad2Deg, 360f);
        int i0 = Mathf.FloorToInt(deg) % 360, i1 = (i0 + 1) % 360;
        return Mathf.Lerp(plazaRadiusByDeg[i0], plazaRadiusByDeg[i1], deg - Mathf.Floor(deg));
    }

    bool InPlaza(Vector3 p, float margin)
    {
        var v = Flat(p - plazaCenter);
        return v.magnitude <= PlazaRadius(Mathf.Atan2(v.z, v.x)) + margin;
    }

    void BuildStreets()
    {
        // 1) Angles where main streets leave the plaza: random, with a minimum gap, never a "+" cross.
        float minGap = Mathf.Min(50f, 360f / streetCount * 0.8f);
        List<float> angles = null;
        for (int tries = 0; tries < 500 && angles == null; tries++)
        {
            var cand = Enumerable.Range(0, streetCount).Select(_ => Range(0f, 360f)).OrderBy(a => a).ToList();
            var gaps = Gaps(cand);
            bool ok = gaps.All(g => g >= minGap);
            bool looksLikeCross = streetCount == 4 && gaps.All(g => g > 75f && g < 105f);
            if (ok && !looksLikeCross) angles = cand;
        }
        if (angles == null) // fallback: evenly spaced with jitter
            angles = Enumerable.Range(0, streetCount).Select(i => i * 360f / streetCount + Range(-8f, 8f)).ToList();
        streetAngles.AddRange(angles);
        Debug.Log($"[StoneVillage] seed {seed}: street angles = {string.Join(", ", angles.Select(a => a.ToString("F0") + "°"))}" +
                  $"  gaps = {string.Join(", ", Gaps(angles).Select(g => g.ToString("F0") + "°"))}");

        // 2) One curved street per angle.
        foreach (float a in angles)
        {
            var st = BuildMainStreet(a);
            mainStreets.Add(st);
            stone.Add(st);
        }

        // 3) Forks and a partial ring make irregular blocks.
        AddForks();
        AddRing();

        foreach (var st in stone)
        {
            stoneMB.AddRibbon(st.pts, st.widths, YStone);
            grassDarkMB.AddRibbon(st.pts, st.widths.Select(w => w + 1.6f).ToList(), YPatchGrass);
        }
    }

    static List<float> Gaps(List<float> sortedAngles)
    {
        var gaps = new List<float>();
        for (int i = 0; i < sortedAngles.Count; i++)
        {
            float next = i == sortedAngles.Count - 1 ? sortedAngles[0] + 360f : sortedAngles[i + 1];
            gaps.Add(next - sortedAngles[i]);
        }
        return gaps;
    }

    Track BuildMainStreet(float angleDeg)
    {
        float a = angleDeg * Mathf.Deg2Rad;
        float pr = PlazaRadius(a);
        // Starts inside the plaza (hidden under it) so there's no seam.
        var ctrl = new List<Vector3> { plazaCenter + Dir(a) * pr * 0.4f, plazaCenter + Dir(a) * pr };
        float endDist = R * Range(0.85f, 0.98f);
        int k = rng.Next(3, 5);
        float seg = (endDist - pr) / k;
        float heading = a;
        Vector3 p = ctrl[1];
        for (int i = 0; i < k; i++)
        {
            // The heading wanders but stays within ±20° of the start so streets don't cross each other.
            heading = a + Mathf.Clamp(heading - a + Range(-18f, 18f) * Mathf.Deg2Rad, -20f * Mathf.Deg2Rad, 20f * Mathf.Deg2Rad);
            p += Dir(heading) * seg + Perp(heading) * Range(-2.5f, 2.5f);
            ctrl.Add(p);
        }
        return MakeTrack(ctrl, 3.0f, 2.2f, "street");
    }

    void AddForks()
    {
        int forks = rng.Next(1, 3);
        foreach (var parent in mainStreets.OrderBy(_ => rng.Next()).ToList())
        {
            if (forks == 0) break;
            int i = Mathf.Clamp(Mathf.RoundToInt(parent.pts.Count * Range(0.45f, 0.6f)), 2, parent.pts.Count - 3);
            Vector3 basePt = parent.pts[i];
            Vector3 t = Flat(parent.pts[i + 1] - parent.pts[i - 1]).normalized;
            float h = Mathf.Atan2(t.z, t.x);

            foreach (float side in new[] { 1f, -1f }.OrderBy(_ => rng.Next()))
            {
                float bh = h + side * Range(30f, 45f) * Mathf.Deg2Rad;
                float len = R * Range(0.3f, 0.42f);
                var ctrl = new List<Vector3> { basePt };
                Vector3 p = basePt;
                for (int k = 0; k < 3; k++)
                {
                    bh += Range(-12f, 12f) * Mathf.Deg2Rad;
                    p += Dir(bh) * (len / 3f);
                    ctrl.Add(p);
                }
                if (Flat(ctrl[ctrl.Count - 1]).magnitude > R * 0.97f)
                    ctrl[ctrl.Count - 1] = Flat(ctrl[ctrl.Count - 1]).normalized * R * 0.97f;

                var fork = MakeTrack(ctrl, parent.widths[i] * 0.85f, 2.0f, "fork");
                if (Clashes(fork, parent, 4, 5f)) continue;
                stone.Add(fork);
                forks--;
                break;
            }
        }
    }

    void AddRing()
    {
        if (!Chance(0.65f)) return;
        int n = mainStreets.Count;
        foreach (int i in Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToList())
        {
            int j = (i + 1) % n;
            float gap = Mathf.Repeat(streetAngles[j] - streetAngles[i], 360f);
            if (gap < 40f || gap > 150f) continue;
            Vector3 a = PointAtFrac(mainStreets[i], Range(0.5f, 0.65f));
            Vector3 b = PointAtFrac(mainStreets[j], Range(0.5f, 0.65f));
            Vector3 mid = (a + b) * 0.5f;
            mid = plazaCenter + Flat(mid - plazaCenter) * 1.18f; // bow outward => curved
            var ring = MakeTrack(new List<Vector3> { a, mid, b }, 2.4f, 2.4f, "ring");
            if (Clashes(ring, null, 4, 4f, mainStreets[i], mainStreets[j])) continue;
            stone.Add(ring);
            Debug.Log($"[StoneVillage] partial ring street between street {i} and {j}");
            return;
        }
    }

    /// True if the track (ignoring its first/last 'skip' points) comes closer than 'dist' to another stone street.
    bool Clashes(Track t, Track ignore, int skip, float dist, Track ignore2 = null, Track ignore3 = null)
    {
        foreach (var other in stone)
        {
            if (other == ignore || other == ignore2 || other == ignore3) continue;
            for (int k = skip; k < t.pts.Count - skip; k++)
                if (DistToTrack(other, t.pts[k]) < dist) return true;
        }
        return false;
    }

    void BuildRoadsAndStream()
    {
        // Each main street continues as a dirt country road out of the village.
        foreach (var st in mainStreets)
        {
            Vector3 end = st.pts[st.pts.Count - 1];
            Vector3 prev = st.pts[Mathf.Max(0, st.pts.Count - 4)];
            float heading = Mathf.Atan2(end.z - prev.z, end.x - prev.x);
            float len = R * Range(0.55f, 0.8f);
            var ctrl = new List<Vector3> { end - Dir(heading) * 1.5f, end };
            Vector3 p = end;
            for (int i = 0; i < 3; i++)
            {
                heading += Range(-20f, 20f) * Mathf.Deg2Rad;
                p += Dir(heading) * (len / 3f);
                ctrl.Add(p);
            }
            roads.Add(MakeTrack(ctrl, 2.4f, 2.0f, "road"));
            // Worn dirt where the paving ends.
            dirtPatchMB.AddFan(end, JitterRing(end, 2.6f, 12), YPatchDirt);
        }

        // A stream that crosses one of the country roads outside the village.
        var host = roads[rng.Next(roads.Count)];
        int ci = host.pts.FindIndex(q => Flat(q).magnitude >= R * 1.2f);
        if (ci < 1) ci = host.pts.Count * 2 / 3;
        ci = Mathf.Clamp(ci, 1, host.pts.Count - 2);
        Vector3 x = host.pts[ci];
        Vector3 roadDir = Flat(host.pts[ci + 1] - host.pts[ci - 1]).normalized;
        Vector3 d = Quaternion.Euler(0f, Range(70f, 110f), 0f) * roadDir;
        var sctrl = new List<Vector3>();
        for (int j = -2; j <= 2; j++)
        {
            var q = x + d * (j * R * 0.5f);
            if (j != 0) q += RightOf(d) * Range(-5f, 5f);
            sctrl.Add(q);
        }
        var stream = MakeTrack(sctrl, 3.2f, 3.2f, "water");
        for (int i = 0; i < stream.widths.Count; i++) stream.widths[i] = 3.2f + 0.6f * Mathf.Sin(i * 0.3f);
        water.Add(stream);
        bankMB.AddRibbon(stream.pts, stream.widths.Select(w => w + 2.2f).ToList(), YBank);
        waterMB.AddRibbon(stream.pts, stream.widths, YWater);

        // Bridges wherever a road crosses the stream.
        foreach (var road in roads)
            for (int i = 0; i < road.pts.Count - 1; i++)
            for (int k = 0; k < stream.pts.Count - 1; k++)
                if (SegmentsCross(road.pts[i], road.pts[i + 1], stream.pts[k], stream.pts[k + 1], out Vector3 hit)
                    && bridges.All(b => Flat(b - hit).magnitude > 8f))
                {
                    bridges.Add(hit);
                    PlaceBridge(hit, Flat(road.pts[i + 1] - road.pts[i]).normalized, stream.widths[k]);
                }

        // Roads are cut where the bridge is.
        foreach (var road in roads)
        {
            roadMB.AddRibbon(road.pts, road.widths, YLane, false, q => bridges.All(b => Flat(q - b).magnitude > 3.6f));
            grassDarkMB.AddRibbon(road.pts, road.widths.Select(w => w + 1.0f).ToList(), YPatchGrass);
        }

        // Water plants and rocks along the stream.
        string[] plants = { "waterlily_A", "waterlily_B", "waterplant_A", "waterplant_B", "waterplant_C" };
        for (int i = 0; i < 16; i++)
        {
            int idx = rng.Next(stream.pts.Count - 1);
            Vector3 t = Flat(stream.pts[idx + 1] - stream.pts[idx]).normalized;
            float off = Range(-0.35f, 0.35f) * stream.widths[idx];
            Vector3 p = stream.pts[idx] + RightOf(t) * off;
            if (bridges.Any(b => Flat(p - b).magnitude < 4f)) continue;
            string m = Pick(plants);
            Spawn(m, gNature, p + Vector3.up * YWater, Yaw(), Vector3.one * ScaleMax(m, m.StartsWith("waterlily") ? 0.7f : 0.9f));
        }
        for (int i = 0; i < 10; i++)
        {
            int idx = rng.Next(stream.pts.Count - 1);
            Vector3 t = Flat(stream.pts[idx + 1] - stream.pts[idx]).normalized;
            float side = Chance(0.5f) ? 1f : -1f;
            Vector3 p = stream.pts[idx] + RightOf(t) * side * (stream.widths[idx] * 0.5f + Range(0.3f, 1.2f));
            if (Clearance(p, true, false) < 0.5f) continue;
            string m = "rock_single_" + "ABCDE"[rng.Next(5)];
            Spawn(m, gNature, p, Yaw(), Vector3.one * ScaleMax(m, Range(0.5f, 1.1f)));
        }
    }

    void PlaceBridge(Vector3 at, Vector3 dir, float streamWidth)
    {
        const string m = "building_bridge_A";
        var size = Native(m).size;
        float s = (streamWidth + 3.4f) / Mathf.Max(size.x, size.z);
        var br = Spawn(m, gNature, at - Vector3.up * 0.1f, AlongDir(m, dir), Vector3.one * s);
        if (br == null) return;
        br.name = "Stone Bridge";
        // MeshColliders follow the real shape, so the player can walk over the arch.
        foreach (var mf in br.GetComponentsInChildren<MeshFilter>())
            mf.gameObject.AddComponent<MeshCollider>();
        Occupy(at, 3f, -10);
    }

    // =========================================================================================
    // Downtown
    // =========================================================================================

    void BuildDowntown()
    {
        // Building slots live in the gaps between streets, on the plaza edge.
        var slots = new List<float>();
        var gaps = Gaps(streetAngles);
        for (int i = 0; i < streetAngles.Count; i++)
        {
            float start = streetAngles[i], g = gaps[i];
            float arc = g * Mathf.Deg2Rad * (downR + 4f);
            int n = Mathf.Clamp(Mathf.FloorToInt((arc - 6f) / 10f), 0, 3);
            for (int k = 0; k < n; k++) slots.Add(start + g * (k + 1) / (n + 1));
        }
        slots = slots.OrderBy(_ => rng.Next()).ToList();

        var wanted = new (string model, float footprint)[]
        {
            ("building_tavern_yellow", 8f), ("building_market_yellow", 7f),
            ("building_blacksmith_yellow", 7f), ("building_church_yellow", 9f),
        };
        int si = 0;
        foreach (var (model, footprint) in wanted)
        {
            Placed placed = null;
            while (placed == null && si < slots.Count) placed = PlaceDowntownBuilding(model, footprint, slots[si++]);
            if (model.Contains("tavern")) tavern = placed;
            else if (model.Contains("market")) market = placed;
            else if (model.Contains("blacksmith")) blacksmith = placed;
            else church = placed;
            if (placed == null) Debug.LogWarning($"[StoneVillage] no free plaza slot for {model}");
        }

        // The well: near, but not exactly at, the center.
        Vector3 wellPos = plazaCenter + Dir(Range(0f, Mathf.PI * 2f)) * Range(1.5f, 3f);
        var well = Spawn("building_well_yellow", gDowntown, wellPos, Yaw(), Vector3.one * ScaleFootprint("building_well_yellow", 2.6f));
        AddBoxCollider(well);
        Occupy(wellPos, 1.8f, -5);

        // A small stage on one side of the plaza, between a street and a building.
        for (int tries = 0; tries < 30; tries++)
        {
            int gi = rng.Next(gaps.Count);
            float a = (streetAngles[gi] + gaps[gi] * Range(0.2f, 0.35f)) * Mathf.Deg2Rad;
            Vector3 p = plazaCenter + Dir(a) * (PlazaRadius(a) - 3.2f);
            if (Clearance(p, false, false) < 2.8f || Overlaps(p, 2.6f, -1)) continue;
            var stage = Spawn("building_stage_A", gDowntown, p, Quaternion.LookRotation(-Dir(a)),
                              Vector3.one * ScaleFootprint("building_stage_A", 5f));
            AddBoxCollider(stage);
            Occupy(p, 2.8f, -6);
            break;
        }
    }

    Placed PlaceDowntownBuilding(string model, float footprint, float angleDeg)
    {
        float a = angleDeg * Mathf.Deg2Rad;
        Vector3 dir = Dir(a);
        float s = ScaleFootprint(model, footprint);
        var size = Native(model).size;
        float w = size.x * s, d = size.z * s;
        float r = 0.5f * Mathf.Max(w, d) * 1.1f;
        for (float push = 0f; push <= 4f; push += 1f)
        {
            Vector3 pos = plazaCenter + dir * (PlazaRadius(a) + d * 0.5f + 0.4f + push);
            if (Clearance(pos, false, true) < r * 0.85f || Overlaps(pos, r, -1)) continue;
            // Door (local +Z) faces the plaza center.
            var rot = Quaternion.LookRotation(-dir);
            var go = Spawn(model, gDowntown, pos, rot, Vector3.one * s);
            if (go == null) return null;
            AddBoxCollider(go);
            int id = nextOwner++;
            Occupy(pos, r, id);
            return new Placed { go = go, pos = pos, fwd = -dir, right = RightOf(-dir), w = w, d = d, h = size.y * s, id = id };
        }
        return null;
    }

    void BuildDowntownProps()
    {
        if (tavern != null) Torches(tavern);
        if (blacksmith != null) { Torches(blacksmith); BlacksmithYard(blacksmith); }
        if (market != null) MarketStalls(market);
        foreach (var b in new[] { tavern, market, blacksmith, church })
            if (b != null) Flags(b);
    }

    void Flags(Placed b)
    {
        const string m = "flag_yellow";
        float s = 3.4f / Mathf.Max(0.01f, Native(m).size.y);
        foreach (float side in new[] { -1f, 1f })
        {
            Vector3 p = b.pos + b.fwd * (b.d * 0.5f + 0.6f) + b.right * side * (b.w * 0.5f - 0.2f);
            Spawn(m, gDowntown, p, Quaternion.LookRotation(b.fwd), Vector3.one * s);
        }
    }

    void Torches(Placed b)
    {
        foreach (float side in new[] { -1f, 1f })
        {
            bool lit = side > 0f || Chance(0.6f);
            string m = lit ? "torch" : "torch_burnt";
            Vector3 wall = b.pos + b.fwd * (b.d * 0.5f) + b.right * side * Mathf.Min(1.5f, b.w * 0.3f);

            // A small wooden bracket from the wall holds the torch.
            var bracket = WoodBeam("Torch Bracket", gDowntown, wall + b.fwd * 0.2f + Vector3.up * 1.6f,
                                   Quaternion.LookRotation(b.fwd), new Vector3(0.08f, 0.08f, 0.7f), false);

            float tilt = 20f * Mathf.Deg2Rad;
            Vector3 up = (Vector3.up * Mathf.Cos(tilt) + b.fwd * Mathf.Sin(tilt)).normalized;
            var rot = Quaternion.FromToRotation(Vector3.up, up) * Upright(m);
            var t = Spawn(m, bracket.parent, wall + b.fwd * 0.45f + Vector3.up * 1.6f, rot,
                          Vector3.one * ScaleMax(m, 0.6f), false);
            if (t == null) continue;
            var tb = WorldBounds(t);
            t.transform.position += Vector3.up * (1.6f - tb.min.y);
            if (lit) AddPointLight(WorldBounds(t).max - Vector3.up * 0.05f, 5f, 2.5f);
        }
    }

    void BlacksmithYard(Placed b)
    {
        foreach (float side in new[] { 1f, -1f }.OrderBy(_ => rng.Next()))
        {
            Vector3 r = b.right * side, f = b.fwd;
            Vector3 basePt = b.pos + r * (b.w * 0.5f + 2.0f) + f * (b.d * 0.15f);
            if (Clearance(basePt) < 2.2f || Overlaps(basePt, 1.8f, b.id) || InPlaza(basePt, -1f)) continue;

            var yard = Group("Blacksmith Yard", gDowntown);
            var anvil = Spawn("anvil", yard, basePt, Yaw(), Vector3.one * ScaleMax("anvil", 0.85f));
            AddBoxCollider(anvil);
            PutOnTop(anvil, "hammer", ToolSize("hammer"), yard, true);
            PutOnTop(anvil, "tongs", ToolSize("tongs"), yard, true);

            var grind = Spawn("grindstone", yard, basePt + f * 1.5f + r * 0.4f, Yaw(), Vector3.one * ScaleMax("grindstone", 1.0f));
            AddBoxCollider(grind);
            Spawn("bucket_metal", yard, basePt - f * 1.2f + r * 0.6f, Yaw(), Vector3.one * ScaleMax("bucket_metal", 0.45f));
            var crate = Spawn("crate_A_small", yard, basePt + r * 1.4f - f * 0.3f, Yaw(), Vector3.one * ScaleMax("crate_A_small", 0.75f));
            AddBoxCollider(crate);
            PutOnTop(crate, "file", ToolSize("file"), yard, true);
            PutOnTop(crate, "chisel", ToolSize("chisel"), yard, true);
            var barrel = Spawn("barrel", yard, basePt - f * 1.3f - r * 0.8f, Yaw(), Vector3.one * ScaleMax("barrel", 1.0f));
            AddBoxCollider(barrel);
            var rack = Spawn("weaponrack", yard, b.pos + r * (b.w * 0.5f + 0.6f) - f * (b.d * 0.2f),
                             Quaternion.LookRotation(r), Vector3.one * ScaleMax("weaponrack", 1.5f));
            AddBoxCollider(rack);
            Occupy(basePt, 2.2f, b.id);
            return;
        }
    }

    static readonly string[] MarketItems =
    {
        "map", "map_rolled", "map_empty", "journal_open", "journal_closed", "blueprint", "blueprint_stacked",
        "drafting_compass", "compass_base", "magnifying_glass", "pencil_A_long", "pencil_B_short", "lantern", "rope_bundle_B",
    };

    void MarketStalls(Placed m)
    {
        var group = Group("Market Stalls", gDowntown);
        float ma = Mathf.Atan2(m.pos.z - plazaCenter.z, m.pos.x - plazaCenter.x);
        int count = rng.Next(3, 5);
        foreach (float off in new[] { -30f, -12f, 12f, 30f }.OrderBy(_ => rng.Next()).Take(count))
        {
            float a = ma + (off + Range(-5f, 5f)) * Mathf.Deg2Rad;
            Vector3 p = plazaCenter + Dir(a) * (PlazaRadius(a) - Range(3.0f, 3.8f));
            // Keep street mouths free: stalls must be clear of the stone streets that enter the plaza.
            if (Clearance(p, false, false) < 1.6f || Overlaps(p, 1.6f, -1)) continue;

            Vector3 face = Flat(plazaCenter - p).normalized; // customers stand on the plaza side
            Vector3 right = RightOf(face);
            var stall = Group("Stall", group);
            var surfaces = new List<GameObject>();

            switch (rng.Next(3))
            {
                case 0: // tent + counter
                    AddBoxCollider(Spawn("tent", stall, p - face * 0.8f, Quaternion.LookRotation(face), Vector3.one * ScaleFootprint("tent", 2.8f)));
                    surfaces.Add(Spawn("crate_long_A", stall, p + face * 0.7f, AlongDir("crate_long_A", right), Vector3.one * ScaleMax("crate_long_A", 1.7f)));
                    break;
                case 1: // pallet with a big crate + a long crate beside it
                    var pallet = Spawn("pallet", stall, p, Quaternion.LookRotation(face) * Quaternion.Euler(0f, Range(-10f, 10f), 0f), Vector3.one * ScaleMax("pallet", 1.4f));
                    surfaces.Add(PutOnTop(pallet, "crate_B_big", 1.0f, stall, false, 0.1f));
                    surfaces.Add(Spawn("crate_long_B", stall, p + right * 1.3f, AlongDir("crate_long_B", face), Vector3.one * ScaleMax("crate_long_B", 1.6f)));
                    break;
                default: // two long crates as a table + sacks behind
                    surfaces.Add(Spawn("crate_long_A", stall, p - right * 0.85f, AlongDir("crate_long_A", right), Vector3.one * ScaleMax("crate_long_A", 1.6f)));
                    surfaces.Add(Spawn("crate_long_B", stall, p + right * 0.85f, AlongDir("crate_long_B", right), Vector3.one * ScaleMax("crate_long_B", 1.6f)));
                    for (int i = 0; i < rng.Next(1, 3); i++)
                        Spawn("sack", stall, p - face * 1.1f + right * Range(-1f, 1f), Yaw(), Vector3.one * ScaleMax("sack", 0.7f));
                    break;
            }

            foreach (var s in surfaces.Where(x => x != null))
            {
                AddBoxCollider(s);
                for (int i = 0; i < rng.Next(2, 5); i++)
                {
                    string item = Pick(MarketItems);
                    PutOnTop(s, item, ToolSize(item), stall, item != "lantern");
                }
            }
            Occupy(p, 1.8f, -3);
        }
    }

    // =========================================================================================
    // Houses
    // =========================================================================================

    class HouseRoll { public string model; public Vector3 scale; public float w, d, h; }

    HouseRoll RollHouse()
    {
        var hr = new HouseRoll { model = Chance(0.55f) ? "building_home_A_yellow" : "building_home_B_yellow" };
        var size = Native(hr.model).size;
        float s = 5.2f / Mathf.Max(size.x, size.z) * Range(0.85f, 1.2f);
        // Non-uniform scale: width, height and depth vary separately.
        hr.scale = new Vector3(s * Range(0.92f, 1.08f), s * Range(0.85f, 1.2f), s * Range(0.92f, 1.08f));
        hr.w = size.x * hr.scale.x;
        hr.d = size.z * hr.scale.z;
        hr.h = size.y * hr.scale.y;
        return hr;
    }

    void BuildHouses()
    {
        int outlyingTarget = Mathf.Max(2, Mathf.RoundToInt(houseCount * 0.15f));
        int alongTarget = houseCount - outlyingTarget;

        // Walk all stone streets "round-robin" so houses spread over every street.
        var cursors = stone.Select(st =>
        {
            int first = st.pts.FindIndex(p => !InPlaza(p, 0f));
            return first < 0 ? float.MaxValue : ArcLengthAt(st, first) + 4f;
        }).ToList();

        int placed = 0;
        for (int pass = 0; pass < 200 && placed < alongTarget; pass++)
        {
            bool any = false;
            for (int si = 0; si < stone.Count && placed < alongTarget; si++)
            {
                var st = stone[si];
                float len = TrackLength(st);
                if (cursors[si] >= len - 3f) continue;
                any = true;
                float s = cursors[si];
                cursors[si] += Range(6.5f, 10f);
                PointAt(st, s, out Vector3 p, out Vector3 tan, out float hw);

                // Denser near downtown: the further out, the more spots are skipped.
                float skip = Mathf.Lerp(0.05f, 0.5f, Flat(p).magnitude / R);
                foreach (float side in new[] { 1f, -1f }.OrderBy(_ => rng.Next()))
                {
                    if (placed >= alongTarget || Chance(skip)) continue;
                    var hr = RollHouse();
                    Vector3 right = RightOf(tan);
                    Vector3 pos = p + right * side * (hw + Range(2.5f, 5.5f) + hr.d * 0.5f);
                    var rot = Quaternion.LookRotation(-right * side) * Quaternion.Euler(0f, Range(-15f, 15f), 0f);
                    if (TryPlaceHouse(hr, pos, rot, 0.6f)) placed++;
                }
            }
            if (!any) break;
        }

        // A few scattered houses further out, with longer winding lanes.
        for (int tries = 0; tries < 400 && placed < houseCount; tries++)
        {
            Vector3 pos = Dir(Range(0f, Mathf.PI * 2f)) * R * Range(0.65f, 1.0f);
            Vector3 target = ClosestOnTracks(stone, pos);
            Vector3 toStreet = Flat(target - pos).normalized;
            if (toStreet == Vector3.zero) continue;
            var rot = Quaternion.LookRotation(toStreet) * Quaternion.Euler(0f, Range(-15f, 15f), 0f);
            if (TryPlaceHouse(RollHouse(), pos, rot, 2.5f)) placed++;
        }

        Debug.Log($"[StoneVillage] houses placed: {placed}/{houseCount}");
    }

    bool TryPlaceHouse(HouseRoll hr, Vector3 pos, Quaternion rot, float laneWiggle)
    {
        float rOcc = 0.5f * Mathf.Max(hr.w, hr.d) * 1.1f;
        float rStreet = 0.5f * Mathf.Sqrt(hr.w * hr.w + hr.d * hr.d) * 0.85f;
        if (Flat(pos).magnitude > R * 1.05f) return false;
        if (InPlaza(pos, rStreet + 1f)) return false;
        if (Clearance(pos) < rStreet) return false;   // tests the real street shapes, not boxes
        if (Overlaps(pos, rOcc + 1f, -1)) return false;

        Vector3 fwd = rot * Vector3.forward, right = rot * Vector3.right;
        Vector3 door = pos + fwd * (hr.d * 0.5f + 0.2f);
        Vector3 target = ClosestOnTracks(stone, door);
        var lane = MakeLane(door, fwd, target, laneWiggle);
        for (int i = 3; i < lane.pts.Count; i++)
            if (Overlaps(lane.pts[i], 0.6f, -1)) return false;

        int id = nextOwner++;
        houseIndex++;
        var group = Group($"House_{houseIndex:00}", gHouses);
        int tint = rng.Next(TintPalette.Length);
        var main = Spawn(hr.model, group, pos, rot, hr.scale);
        Tint(main, tint);
        AddBoxCollider(main);
        Occupy(pos, rOcc, id);

        lanes.Add(lane);
        laneMB.AddRibbon(lane.pts, lane.widths, YLane);
        grassDarkMB.AddRibbon(lane.pts, lane.widths.Select(w => w + 0.9f).ToList(), YPatchGrass);
        grassDarkMB.AddFan(pos, JitterRing(pos, rOcc + 0.8f, 14), YPatchGrass);

        AddHouseExtras(hr, group, pos, rot, fwd, right, tint, id);
        BuildYard(hr, group, pos, fwd, right, id);
        return true;
    }

    Track MakeLane(Vector3 door, Vector3 fwd, Vector3 target, float wiggle)
    {
        Vector3 a = door + fwd * 1.2f;
        var ctrl = new List<Vector3> { door, a };
        Vector3 toT = Flat(target - a);
        int mids = toT.magnitude > 14f ? 2 : (toT.magnitude > 5f ? 1 : 0);
        Vector3 side = RightOf(toT.normalized);
        for (int i = 1; i <= mids; i++)
            ctrl.Add(Vector3.Lerp(a, target, i / (float)(mids + 1)) + side * Range(-wiggle, wiggle));
        ctrl.Add(target);
        return MakeTrack(ctrl, 1.3f, 1.05f, "lane", 0.6f);
    }

    void AddHouseExtras(HouseRoll hr, Transform group, Vector3 pos, Quaternion rot, Vector3 fwd, Vector3 right, int tint, int id)
    {
        float roll = (float)rng.NextDouble();
        float side = Chance(0.5f) ? 1f : -1f;

        if (roll < 0.25f) // smaller house attached as an extension
        {
            string m = hr.model.Contains("_A_") ? "building_home_B_yellow" : "building_home_A_yellow";
            var size = Native(m).size;
            float s = hr.w * Range(0.55f, 0.7f) / Mathf.Max(size.x, size.z);
            var sc = new Vector3(s, s * Range(0.8f, 1f), s);
            float w2 = size.x * s, d2 = size.z * s;
            Vector3 p = pos + right * side * (hr.w * 0.5f + w2 * 0.5f - 0.3f) - fwd * (hr.d - d2) * 0.5f;
            var rot2 = Chance(0.5f) ? rot : rot * Quaternion.Euler(0f, 90f * side, 0f);
            float r2 = 0.5f * Mathf.Max(w2, d2) * 1.1f;
            if (Clearance(p) > r2 * 0.8f && !Overlaps(p, r2 + 0.5f, id) && !InPlaza(p, r2))
            {
                var e = Spawn(m, group, p, rot2, sc);
                Tint(e, tint);
                AddBoxCollider(e);
                Occupy(p, r2, id);
            }
        }
        else if (roll < 0.4f) // corner tower
        {
            string m = Chance(0.5f) ? "building_tower_A_yellow" : "building_tower_B_yellow";
            Vector3 p = pos - fwd * (hr.d * 0.5f - 0.6f) + right * side * (hr.w * 0.5f - 0.4f);
            if (Clearance(p) > 1.4f && !Overlaps(p, 1.4f, id))
            {
                var t = Spawn(m, group, p, rot, Vector3.one * ScaleFootprint(m, Range(2.2f, 2.8f)));
                Tint(t, tint);
                AddBoxCollider(t);
                Occupy(p, 1.6f, id);
            }
        }
        else if (roll < 0.5f) // barn
        {
            const string m = "building_grain";
            Vector3 p = pos + right * side * (hr.w * 0.5f + 3.2f) - fwd * 1.0f;
            if (Clearance(p) > 2.6f && !Overlaps(p, 2.6f, id) && !InPlaza(p, 3f))
            {
                var b = Spawn(m, group, p, rot * Quaternion.Euler(0f, Range(-20f, 20f), 0f), Vector3.one * ScaleFootprint(m, Range(4f, 5f)));
                AddBoxCollider(b);
                Occupy(p, 2.8f, id);
            }
        }
    }

    void BuildYard(HouseRoll hr, Transform group, Vector3 pos, Vector3 fwd, Vector3 right, int id)
    {
        var yard = Group("Yard", group);
        int kits = rng.Next(3, 7);
        for (int k = 0; k < kits; k++)
        {
            int kind = rng.Next(0, 8);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float sx = Chance(0.5f) ? 1f : -1f;
                int slot = kind == 3 ? 0 : rng.Next(0, 3); // leaning tools always go against a side wall
                Vector3 local;
                if (slot == 0) local = new Vector3(sx * (hr.w * 0.5f + (kind == 3 ? 0.12f : Range(0.6f, 1.4f))), 0f, Range(-hr.d * 0.4f, hr.d * 0.3f));
                else if (slot == 1) local = new Vector3(Range(-hr.w * 0.45f, hr.w * 0.45f), 0f, -(hr.d * 0.5f + Range(0.7f, 1.8f)));
                else local = new Vector3(sx * Range(1.2f, hr.w * 0.5f + 0.6f), 0f, hr.d * 0.5f + Range(0.5f, 1.0f));

                Vector3 p = pos + right * local.x + fwd * local.z;
                if (Clearance(p) < 0.5f || InPlaza(p, 0.5f) || Overlaps(p, 0.6f, id)) continue;
                Vector3 awayFromWall = slot == 0 ? right * sx : (slot == 1 ? -fwd : fwd);
                PlaceYardKit(kind, yard, p, awayFromWall);
                Occupy(p, 0.7f, id);
                break;
            }
        }

        if (Chance(0.6f)) // lantern post next to the door
        {
            float side = Chance(0.5f) ? 1f : -1f;
            Vector3 p = pos + fwd * (hr.d * 0.5f + 0.9f) + right * side * 1.1f;
            if (Clearance(p) > 0.3f && !Overlaps(p, 0.4f, id))
                LanternPost(yard, p, -right * side, Chance(0.5f), 2.2f);
        }

        if (Chance(0.4f)) BuildBackFence(hr, yard, pos, fwd, right, id);
    }

    void PlaceYardKit(int kind, Transform parent, Vector3 p, Vector3 awayFromWall)
    {
        switch (kind)
        {
            case 0: // barrels
                for (int i = 0; i < rng.Next(1, 4); i++)
                    AddBoxCollider(Spawn("barrel", parent, p + RandomInCircle(0.7f), Yaw(), Vector3.one * ScaleMax("barrel", Range(0.9f, 1.05f))));
                break;
            case 1: // crate stack
                var big = Spawn("crate_B_big", parent, p, Yaw(), Vector3.one * ScaleMax("crate_B_big", 1.0f));
                AddBoxCollider(big);
                if (Chance(0.7f)) PutOnTop(big, "crate_A_small", 0.6f, parent, false, 0.3f);
                break;
            case 2: // sacks and a bucket
                for (int i = 0; i < rng.Next(1, 4); i++)
                    Spawn("sack", parent, p + RandomInCircle(0.6f), Yaw(), Vector3.one * ScaleMax("sack", 0.7f));
                if (Chance(0.5f)) Spawn("bucket_water", parent, p + RandomInCircle(0.8f), Yaw(), Vector3.one * ScaleMax("bucket_water", 0.45f));
                break;
            case 3: // a tool leaning on the wall
                string tool = Pick(new[] { "shovel", "pickaxe", "axe", "saw", "mallet", "hammer" });
                LeanTool(tool, parent, p, awayFromWall, ToolSize(tool));
                break;
            case 4: // wheelbarrow
                AddBoxCollider(Spawn("wheelbarrow", parent, p, Yaw(), Vector3.one * ScaleMax("wheelbarrow", 1.5f)));
                if (Chance(0.5f)) Spawn("bucket_empty", parent, p + RandomInCircle(1f), Yaw(), Vector3.one * ScaleMax("bucket_empty", 0.45f));
                break;
            case 5: // small tree
                string tree = Chance(0.5f) ? "tree_single_A" : "tree_single_B";
                AddTrunkCollider(Spawn(tree, parent, p, Yaw(), Vector3.one * ScaleMax(tree, Range(3f, 4.2f))));
                break;
            case 6: // rocks
                for (int i = 0; i < rng.Next(1, 3); i++)
                {
                    string rock = "rock_single_" + "ABCDE"[rng.Next(5)];
                    Spawn(rock, parent, p + RandomInCircle(0.6f), Yaw(), Vector3.one * ScaleMax(rock, Range(0.4f, 0.9f)));
                }
                break;
            default: // woodpile with an axe
                var lumber = Spawn("resource_lumber", parent, p, Yaw(), Vector3.one * ScaleMax("resource_lumber", 1.6f));
                AddBoxCollider(lumber);
                PutOnTop(lumber, "axe", ToolSize("axe"), parent, true, 0.3f);
                break;
        }
    }

    void BuildBackFence(HouseRoll hr, Transform parent, Vector3 pos, Vector3 fwd, Vector3 right, int id)
    {
        float x = hr.w * 0.5f + 1.0f;
        float zBack = -(hr.d * 0.5f + Range(2.5f, 3.5f));
        float zFront = -hr.d * 0.5f + 0.4f;
        // Local function (like a closure/arrow function in JS): converts house-local XZ to world.
        Vector3 W(float lx, float lz) => pos + right * lx + fwd * lz;
        var fence = Group("Fence", parent);
        FenceLine(fence, W(-x, zFront), W(-x, zBack), id);
        FenceLine(fence, W(-x, zBack), W(x, zBack), id);
        FenceLine(fence, W(x, zBack), W(x, zFront), id);
    }

    void FenceLine(Transform parent, Vector3 a, Vector3 b, int id)
    {
        const string m = "fence_wood_straight";
        var size = Native(m).size;
        float nativeLen = Mathf.Max(size.x, size.z);
        float len = Flat(b - a).magnitude;
        int n = Mathf.Max(1, Mathf.RoundToInt(len / 2f));
        float seg = len / n;
        Vector3 dir = Flat(b - a).normalized;
        for (int i = 0; i < n; i++)
        {
            if (Chance(0.18f)) continue; // gaps
            Vector3 c = a + dir * (seg * (i + 0.5f));
            if (Clearance(c) < 0.3f || InPlaza(c, 0.3f) || Overlaps(c, 0.4f, id)) continue;
            AddBoxCollider(Spawn(m, parent, c, AlongDir(m, dir) * Quaternion.Euler(0f, Range(-3f, 3f), 0f), Vector3.one * (seg / nativeLen)));
        }
    }

    // =========================================================================================
    // Street dressing
    // =========================================================================================

    void BuildCurbs()
    {
        // Curbs: thin dark-stone strips on both street edges and around the plaza, with holes where
        // another street or a dirt lane joins.
        foreach (var st in stone)
            foreach (float side in new[] { -1f, 1f })
            {
                var edge = new List<Vector3>();
                for (int i = 0; i < st.pts.Count; i++)
                {
                    Vector3 prev = st.pts[Mathf.Max(0, i - 1)], next = st.pts[Mathf.Min(st.pts.Count - 1, i + 1)];
                    Vector3 r = RightOf(Flat(next - prev).normalized);
                    edge.Add(st.pts[i] + r * side * (st.widths[i] * 0.5f + 0.12f));
                }
                curbMB.AddRibbon(edge, edge.Select(_ => 0.3f).ToList(), YCurb, false,
                    q => !InPlaza(q, 0.2f) && stone.All(o => DistToTrack(o, q) > 0.02f) && lanes.All(l => DistToTrack(l, q) > 0.1f));
            }

        curbMB.AddRibbon(plazaRing, plazaRing.Select(_ => 0.35f).ToList(), YCurb, true,
            q => stone.All(o => DistToTrack(o, q) > 0.1f));
    }

    void BuildStreetLanterns()
    {
        var group = Group("Street Lanterns", gProps);
        foreach (var st in stone)
        {
            int first = st.pts.FindIndex(p => !InPlaza(p, 1f));
            if (first < 0) continue;
            float len = TrackLength(st);
            float side = Chance(0.5f) ? 1f : -1f;
            for (float s = ArcLengthAt(st, first) + Range(2f, 5f); s < len - 1f; s += Range(8f, 12f))
            {
                side = -side; // alternate sides
                PointAt(st, s, out Vector3 p, out Vector3 tan, out float hw);
                Vector3 pos = p + RightOf(tan) * side * (hw + 0.6f);
                if (Clearance(pos) < 0.25f || InPlaza(pos, 0.3f) || Overlaps(pos, 0.4f, -1)) continue;
                LanternPost(group, pos, -RightOf(tan) * side, true);
                Occupy(pos, 0.4f, -7);
            }
        }

        // A few around the plaza edge, away from the street mouths.
        int plazaPosts = 0;
        for (int tries = 0; tries < 60 && plazaPosts < 6; tries++)
        {
            float a = Range(0f, Mathf.PI * 2f);
            Vector3 pos = plazaCenter + Dir(a) * (PlazaRadius(a) - 0.7f);
            if (Clearance(pos, false, false) < 0.6f || Overlaps(pos, 1.2f, -1)) continue;
            LanternPost(group, pos, -Dir(a), true);
            Occupy(pos, 0.5f, -7);
            plazaPosts++;
        }
    }

    void BuildBuilderCorner()
    {
        for (int tries = 0; tries < 30; tries++)
        {
            var st = Pick(mainStreets);
            PointAt(st, TrackLength(st) * Range(0.25f, 0.45f), out Vector3 sp, out Vector3 along, out float hw);
            float side = Chance(0.5f) ? 1f : -1f;
            Vector3 n = RightOf(along) * side;
            Vector3 p = sp + n * (hw + 2.4f);
            if (Clearance(p) < 1.6f || Overlaps(p, 2.2f, -1) || InPlaza(p, 2f)) continue;

            var g = Group("Builder Corner", gProps);
            var lumber = Spawn("resource_lumber", g, p, AlongDir("resource_lumber", along), Vector3.one * ScaleMax("resource_lumber", 2.0f));
            AddBoxCollider(lumber);
            PutOnTop(lumber, "saw", ToolSize("saw"), g, true, 0.4f);
            PutOnTop(lumber, "rope_bundle_A", 0.45f, g, false, 0.4f);
            LeanTool("pickaxe", g, p + n * 0.7f + along * Range(-0.6f, -0.2f), n, ToolSize("pickaxe"));
            LeanTool("shovel", g, p + n * 0.7f + along * Range(0.2f, 0.6f), n, ToolSize("shovel"));
            AddBoxCollider(Spawn("wheelbarrow", g, p + along * 1.9f, Yaw(), Vector3.one * ScaleMax("wheelbarrow", 1.5f)));
            var crate = Spawn("crate_B_big", g, p - along * 1.7f, Yaw(), Vector3.one * ScaleMax("crate_B_big", 0.9f));
            AddBoxCollider(crate);
            foreach (var t in new[] { "handplane", "handdrill", "trowel", "chisel", "screwdriver_A_long_color" }.OrderBy(_ => rng.Next()).Take(2))
                PutOnTop(crate, t, ToolSize(t), g, true);
            Spawn("rope_bundle_B", g, p - along * 1.7f - n * 0.9f, Yaw(), Vector3.one * ScaleMax("rope_bundle_B", 0.45f));
            Spawn("bucket_empty", g, p - along * 0.6f - n * 1.0f, Yaw(), Vector3.one * ScaleMax("bucket_empty", 0.45f));
            Occupy(p, 2.6f, -4);
            return;
        }
    }

    // =========================================================================================
    // Nature
    // =========================================================================================

    void BuildNature()
    {
        string[] clusterTrees = { "trees_A_small", "trees_B_small", "trees_A_medium", "trees_B_medium", "trees_A_large", "trees_B_large", "tree_single_A", "tree_single_B" };
        var treeGroup = Group("Trees", gNature);

        // Clusters in an outer ring.
        int clusters = Mathf.RoundToInt(R * 0.35f);
        for (int c = 0; c < clusters; c++)
        {
            Vector3 center = Dir(Range(0f, Mathf.PI * 2f)) * R * Range(0.95f, 1.4f);
            for (int i = 0; i < rng.Next(3, 7); i++)
            {
                Vector3 p = center + RandomInCircle(6f);
                if (Clearance(p) < 1.5f || Overlaps(p, 1.2f, -1) || InPlaza(p, 2f)) continue;
                string m = Pick(clusterTrees);
                float size = m.Contains("large") ? Range(6f, 8f) : m.Contains("medium") ? Range(4.5f, 6f) : Range(3.2f, 4.8f);
                var t = Spawn(m, treeGroup, p, Yaw(), Vector3.one * ScaleMax(m, size));
                if (m.StartsWith("tree_single")) AddTrunkCollider(t);
                Occupy(p, 1.2f, -8);
            }
        }

        // A few single trees inside the village.
        for (int tries = 0, placed = 0; tries < 200 && placed < 12; tries++)
        {
            Vector3 p = Dir(Range(0f, Mathf.PI * 2f)) * R * Range(0.3f, 0.95f);
            if (Clearance(p) < 2f || Overlaps(p, 1.5f, -1) || InPlaza(p, 2f)) continue;
            string m = Chance(0.5f) ? "tree_single_A" : "tree_single_B";
            AddTrunkCollider(Spawn(m, treeGroup, p, Yaw(), Vector3.one * ScaleMax(m, Range(3.5f, 5f))));
            Occupy(p, 1.5f, -8);
            placed++;
        }

        // Rocks.
        var rockGroup = Group("Rocks", gNature);
        for (int tries = 0, placed = 0; tries < 200 && placed < 25; tries++)
        {
            Vector3 p = Dir(Range(0f, Mathf.PI * 2f)) * R * Range(0.3f, 1.5f);
            if (Clearance(p) < 0.8f || Overlaps(p, 0.8f, -1) || InPlaza(p, 1f)) continue;
            string m = "rock_single_" + "ABCDE"[rng.Next(5)];
            float size = Range(0.6f, 1.6f);
            var r = Spawn(m, rockGroup, p, Yaw(), Vector3.one * ScaleMax(m, size));
            if (size > 1f) AddBoxCollider(r);
            placed++;
        }

        // Hills and mountains as a backdrop.
        string[] backdrop = { "hill_single_A", "hill_single_B", "hill_single_C", "hills_B_trees", "hills_C_trees",
                              "mountain_A_grass", "mountain_A_grass_trees", "mountain_B_grass", "mountain_B_grass_trees",
                              "mountain_C_grass", "mountain_C_grass_trees" };
        var hills = Group("Backdrop", gNature);
        int count = rng.Next(12, 17);
        for (int tries = 0, placed = 0; tries < 120 && placed < count; tries++)
        {
            string m = Pick(backdrop);
            float size = m.StartsWith("mountain") ? Range(16f, 26f) : Range(10f, 16f);
            Vector3 p = Dir(Range(0f, Mathf.PI * 2f)) * R * Range(1.5f, 1.95f);
            if (Clearance(p) < size * 0.45f || Overlaps(p, size * 0.35f, -1)) continue;
            Spawn(m, hills, p, Yaw(), Vector3.one * ScaleFootprint(m, size));
            Occupy(p, size * 0.4f, -9);
            placed++;
        }
    }

    // =========================================================================================
    // Meshes, lighting, final touches
    // =========================================================================================

    void BuildMeshObjects()
    {
        MeshObject("Grass Patches", gGround, grassDarkMB, matGrassDark, false, false);
        MeshObject("Dirt Patches", gGround, dirtPatchMB, matDirt, false, false);
        MeshObject("Plaza", gStone, plazaMB, matCobble, true, false);
        MeshObject("Stone Streets", gStone, stoneMB, matCobble, true, false);
        MeshObject("Curbs", gStone, curbMB, matCurb, false, false);
        MeshObject("Dirt Lanes", gDirt, laneMB, matDirt, true, false);
        MeshObject("Country Roads", gDirt, roadMB, matDirt, true, false);
        MeshObject("Stream Banks", gNature, bankMB, matDirt, false, false);
        MeshObject("Stream", gNature, waterMB, matWater, false, false);
    }

    void MeshObject(string name, Transform parent, MeshBuilder mb, Material mat, bool collider, bool shadows)
    {
        if (mb.IsEmpty) return;
        var mesh = mb.Build(name);
        // Save the mesh as an asset, otherwise it would only live in memory and the prefab would lose it.
        AssetDatabase.CreateAsset(mesh, $"{SeedFolder}/{name}.asset");
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    void SetupLighting()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.Log("[StoneVillage] Not in StoneVillage scene: light & volume left untouched.");
            return;
        }

        foreach (var go in scene.GetRootGameObjects())
        {
            foreach (var l in go.GetComponentsInChildren<Light>(true))
            {
                if (l.type != LightType.Directional) continue;
                // Late-afternoon sun: low, warm.
                l.transform.rotation = Quaternion.Euler(32f, -38f, 0f);
                l.color = new Color(1f, 0.86f, 0.68f);
                l.intensity = 1.25f;
                l.shadows = LightShadows.Soft;
            }

            foreach (var v in go.GetComponentsInChildren<Volume>(true))
            {
                if (!v.isGlobal) continue;
                // Use our own copy of the profile so the MedievalVillage scene is not affected.
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
                if (profile == null)
                {
                    if (v.sharedProfile != null) AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(v.sharedProfile), ProfilePath);
                    else AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<VolumeProfile>(), ProfilePath);
                    profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
                }
                v.sharedProfile = profile;

                var bloom = GetOrAdd<Bloom>(profile);
                bloom.threshold.Override(0.95f);
                bloom.intensity.Override(0.45f);
                bloom.scatter.Override(0.65f);
                GetOrAdd<WhiteBalance>(profile).temperature.Override(12f);
                GetOrAdd<ColorAdjustments>(profile).saturation.Override(8f);
                EditorUtility.SetDirty(profile);
            }
        }
    }

    // Generic method (like a TS generic function<T>). 'where T : VolumeComponent' restricts T.
    static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet(out T comp)) { comp.active = true; return comp; }
        comp = profile.Add<T>(true);
        // Volume components are stored inside the profile asset file as sub-assets.
        AssetDatabase.AddObjectToAsset(comp, profile);
        return comp;
    }

    void PlacePlayer()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) return;
        var player = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Player");
        if (player == null) return;
        float a = streetAngles[0] * Mathf.Deg2Rad;
        Vector3 dir = Dir(a);
        player.transform.position = plazaCenter + dir * PlazaRadius(a) * 0.6f + Vector3.up * 0.1f;
        player.transform.rotation = Quaternion.LookRotation(-dir);
    }

    void MarkStatic()
    {
        const StaticEditorFlags flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic |
                                        StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
        foreach (var g in new[] { gGround, gStone, gDirt, gDowntown, gHouses, gProps, gNature })
            foreach (var t in g.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
    }

    void LogSummary()
    {
        int CountModels(Transform t) => t.GetComponentsInChildren<Transform>(true).Count(x => PrefabUtility.IsAnyPrefabInstanceRoot(x.gameObject));
        Debug.Log($"[StoneVillage] seed {seed} done.\n" +
                  $"  Houses: {gHouses.childCount} (models incl. yards: {CountModels(gHouses)})\n" +
                  $"  Downtown models: {CountModels(gDowntown)}\n" +
                  $"  Props models: {CountModels(gProps)}\n" +
                  $"  Nature models: {CountModels(gNature)}\n" +
                  $"  Stone streets: {stone.Count} (main {mainStreets.Count}), dirt lanes: {lanes.Count}, roads: {roads.Count}, bridges: {bridges.Count}\n" +
                  $"  Point lights: {lightCount}");
    }

    // =========================================================================================
    // Spawning helpers
    // =========================================================================================

    static readonly Dictionary<string, GameObject> modelCache = new Dictionary<string, GameObject>();
    static readonly Dictionary<string, Bounds> boundsCache = new Dictionary<string, Bounds>();
    static readonly HashSet<string> toolModels = new HashSet<string>();
    readonly Dictionary<string, Material> tintCache = new Dictionary<string, Material>();
    static readonly Dictionary<string, Material> toolMatCache = new Dictionary<string, Material>();

    static GameObject Model(string name)
    {
        if (modelCache.Count == 0)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { HexRoot, ToolsRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string n = Path.GetFileNameWithoutExtension(path);
                modelCache[n] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (path.StartsWith(ToolsRoot)) toolModels.Add(n);
            }
        }
        if (modelCache.TryGetValue(name, out var go)) return go;
        Debug.LogWarning($"[StoneVillage] model not found: {name}");
        return null;
    }

    /// Size of a model at scale 1, measured from its renderers.
    static Bounds Native(string name)
    {
        if (boundsCache.TryGetValue(name, out var b)) return b;
        var prefab = Model(name);
        b = new Bounds(Vector3.zero, Vector3.one);
        if (prefab != null)
        {
            var temp = Object.Instantiate(prefab);
            b = WorldBounds(temp);
            Object.DestroyImmediate(temp);
            if (b.size == Vector3.zero) b.size = Vector3.one;
        }
        boundsCache[name] = b;
        return b;
    }

    static float ScaleMax(string m, float target) { var s = Native(m).size; return target / Mathf.Max(s.x, s.y, s.z); }
    static float ScaleFootprint(string m, float target) { var s = Native(m).size; return target / Mathf.Max(s.x, s.z); }

    /// Rotation that stands a model up (longest axis vertical) — for leaning tools and torches.
    static Quaternion Upright(string m)
    {
        var s = Native(m).size;
        if (s.y >= s.x && s.y >= s.z) return Quaternion.identity;
        return s.x >= s.z ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(90f, 0f, 0f);
    }

    /// Rotation that lays a model flat (thinnest axis vertical) — for maps and tools on tables.
    static Quaternion LayFlat(string m)
    {
        var s = Native(m).size;
        if (s.y <= s.x && s.y <= s.z) return Quaternion.identity;
        return s.x <= s.z ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(90f, 0f, 0f);
    }

    /// Rotation that points the model's longest horizontal axis along 'dir' (fences, bridge, crates).
    static Quaternion AlongDir(string m, Vector3 dir)
    {
        var s = Native(m).size;
        var look = Quaternion.LookRotation(dir);
        return s.x > s.z ? look * Quaternion.Euler(0f, -90f, 0f) : look;
    }

    static float ToolSize(string m)
    {
        switch (m)
        {
            case "anvil": return 0.85f;
            case "grindstone": return 1.0f;
            case "shovel": return 1.15f;
            case "pickaxe": return 0.9f;
            case "axe": return 0.75f;
            case "saw": return 0.65f;
            case "tongs": return 0.5f;
            case "hammer": case "mallet": return 0.38f;
            case "lantern": return 0.4f;
            case "map": case "map_empty": case "map_rolled": case "blueprint": case "blueprint_stacked": return 0.45f;
            case "rope_bundle_A": case "rope_bundle_B": return 0.45f;
            case "journal_open": return 0.32f;
            case "handplane": case "handdrill": return 0.32f;
            default: return 0.22f; // pencils, compasses, journals, chisels, files...
        }
    }

    /// Instantiates a model linked to its FBX (so changes to the FBX still apply).
    /// By default it moves the object so its lowest point sits exactly at pos.y.
    GameObject Spawn(string model, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, bool snapBottom = true)
    {
        var prefab = Model(model);
        if (prefab == null) return null;
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        if (snapBottom)
        {
            var b = WorldBounds(go);
            go.transform.position += Vector3.up * (pos.y - b.min.y);
        }
        if (toolModels.Contains(model)) FixToolMaterials(go, model);
        return go;
    }

    /// Puts a model on top of another one (using the surface's bounds), with a random spot and yaw.
    GameObject PutOnTop(GameObject surface, string model, float size, Transform parent, bool flat, float spread = 0.6f)
    {
        if (surface == null) return null;
        var b = WorldBounds(surface);
        var p = new Vector3(b.center.x + Range(-1f, 1f) * b.extents.x * spread, b.max.y,
                            b.center.z + Range(-1f, 1f) * b.extents.z * spread);
        var rot = Yaw() * (flat ? LayFlat(model) : Quaternion.identity);
        return Spawn(model, parent, p, rot, Vector3.one * ScaleMax(model, size));
    }

    /// Stands a tool up and tilts it so its top rests against a wall (or a pile).
    void LeanTool(string model, Transform parent, Vector3 wallPoint, Vector3 awayFromWall, float length)
    {
        float tilt = Range(12f, 20f) * Mathf.Deg2Rad;
        Vector3 up = (Vector3.up * Mathf.Cos(tilt) - awayFromWall * Mathf.Sin(tilt)).normalized;
        var rot = Quaternion.FromToRotation(Vector3.up, up) * Yaw() * Upright(model);
        Vector3 basePt = wallPoint + awayFromWall * (length * Mathf.Sin(tilt) * 0.9f);
        Spawn(model, parent, basePt, rot, Vector3.one * ScaleMax(model, length));
    }

    void LanternPost(Transform parent, Vector3 pos, Vector3 armDir, bool withLight, float height = 2.6f)
    {
        var lp = Group("Lantern Post", parent);
        lp.position = pos;
        WoodBeam("Post", lp, pos + Vector3.up * height * 0.5f, Quaternion.identity, new Vector3(0.16f, height, 0.16f), true);
        WoodBeam("Arm", lp, pos + Vector3.up * (height - 0.1f) + armDir * 0.3f, Quaternion.LookRotation(armDir), new Vector3(0.09f, 0.09f, 0.7f), false);
        var lantern = Spawn("lantern", lp, pos + armDir * 0.58f + Vector3.up * (height - 0.6f), Yaw(),
                            Vector3.one * ScaleMax("lantern", 0.42f), false);
        if (lantern == null) return;
        // Hang it: its top touches the bottom of the arm.
        var b = WorldBounds(lantern);
        lantern.transform.position += Vector3.up * ((height - 0.15f) - b.max.y);
        if (withLight) AddPointLight(WorldBounds(lantern).center, 6f, 2f);
    }

    Transform WoodBeam(string name, Transform parent, Vector3 pos, Quaternion rot, Vector3 scale, bool collider)
    {
        var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
        beam.name = name;
        beam.transform.SetParent(parent, false);
        beam.transform.SetPositionAndRotation(pos, rot);
        beam.transform.localScale = scale;
        beam.GetComponent<Renderer>().sharedMaterial = matWood;
        if (!collider) Object.DestroyImmediate(beam.GetComponent<Collider>());
        return beam.transform;
    }

    void AddPointLight(Vector3 p, float range, float intensity)
    {
        var go = new GameObject("Lantern Light");
        go.transform.SetParent(gLights, false);
        go.transform.position = p;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = range;
        l.intensity = intensity;
        l.color = new Color(1f, 0.72f, 0.42f);
        l.shadows = LightShadows.None;
        lightCount++;
    }

    /// Tint = a few shared material variants (one per palette colour), NOT one material per house.
    /// (A MaterialPropertyBlock would be lighter, but it isn't saved into scenes/prefabs, so the tint
    /// would vanish after reopening the scene.)
    void Tint(GameObject go, int index)
    {
        if (go == null) return;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null) mats[i] = TintedMaterial(mats[i], index);
            r.sharedMaterials = mats;
        }
    }

    Material TintedMaterial(Material src, int index)
    {
        string key = src.name + "_" + index;
        if (tintCache.TryGetValue(key, out var cached) && cached != null) return cached;
        string path = $"{MatFolder}/Tints/{Sanitize(src.name)}_Tint{index}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(src);
            var baseColor = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : Color.white;
            m.SetColor("_BaseColor", baseColor * TintPalette[index]);
            AssetDatabase.CreateAsset(m, path);
        }
        tintCache[key] = m;
        return m;
    }

    /// If an RPG Tools FBX imported without a texture (or with a non-URP shader), give it a URP
    /// material that uses the pack's texture atlas.
    static void FixToolMaterials(GameObject go, string model)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                bool ok = m != null && m.shader != null && m.shader.name.StartsWith("Universal Render Pipeline") &&
                          m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null;
                if (ok) continue;
                string hint = ((m != null ? m.name : "") + " " + model).ToLowerInvariant();
                string tex = hint.Contains("blueprint") ? "tools_bits_blueprint"
                           : hint.Contains("map_empty") ? "tools_bits_map_empty"
                           : hint.Contains("map") ? "tools_bits_map"
                           : "tools_bits_texture";
                mats[i] = ToolMaterial(tex);
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    static Material ToolMaterial(string texName)
    {
        if (toolMatCache.TryGetValue(texName, out var cached) && cached != null) return cached;
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ToolsRoot}/{texName}.png");
        var m = MakeMat("RPGTools_" + texName, Color.white, 0.2f, tex);
        toolMatCache[texName] = m;
        return m;
    }

    static string Sanitize(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    static Bounds WorldBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    /// Box collider fitted to the model, in the object's own (local) space.
    static void AddBoxCollider(GameObject go)
    {
        if (go == null) return;
        var toLocal = go.transform.worldToLocalMatrix;
        bool has = false;
        var b = new Bounds();
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mb = mf.sharedMesh.bounds;
            var m = toLocal * mf.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                if (!has) { b = new Bounds(p, Vector3.zero); has = true; }
                else b.Encapsulate(p);
            }
        }
        if (!has) return;
        var bc = go.AddComponent<BoxCollider>();
        bc.center = b.center;
        bc.size = b.size;
    }

    /// Thin capsule at the trunk, so the player bumps into trees but not into the canopy.
    static void AddTrunkCollider(GameObject tree)
    {
        if (tree == null) return;
        float s = Mathf.Max(0.001f, tree.transform.lossyScale.y);
        var cc = tree.AddComponent<CapsuleCollider>();
        cc.radius = 0.35f / s;
        cc.height = 3f / s;
        cc.center = new Vector3(0f, 1.5f / s, 0f);
    }

    void Occupy(Vector3 c, float r, int owner) => occupied.Add(new Circle(Flat(c), r, owner));

    bool Overlaps(Vector3 p, float r, int ignoreOwner)
    {
        p = Flat(p);
        foreach (var o in occupied)
            if (o.owner != ignoreOwner && (o.c - p).magnitude < o.r + r) return true;
        return false;
    }

    // =========================================================================================
    // Track (polyline) geometry
    // =========================================================================================

    Track MakeTrack(List<Vector3> ctrl, float w0, float w1, string kind, float step = 1f)
    {
        var t = new Track { kind = kind, pts = SampleSpline(ctrl, step, false) };
        float total = 0f;
        var cum = new List<float> { 0f };
        for (int i = 1; i < t.pts.Count; i++) { total += Flat(t.pts[i] - t.pts[i - 1]).magnitude; cum.Add(total); }
        foreach (float c in cum) t.widths.Add(Mathf.Lerp(w0, w1, total > 0f ? c / total : 0f));
        return t;
    }

    /// Distance from q to the EDGE of a track (negative = q is on the track).
    static float DistToTrack(Track t, Vector3 q)
    {
        float best = float.MaxValue;
        for (int i = 0; i < t.pts.Count - 1; i++)
        {
            Vector3 a = t.pts[i], b = t.pts[i + 1];
            float u = ProjectT(a, b, q);
            Vector3 c = Vector3.Lerp(a, b, u);
            float hw = Mathf.Lerp(t.widths[i], t.widths[i + 1], u) * 0.5f;
            float d = Flat(q - c).magnitude - hw;
            if (d < best) best = d;
        }
        return best;
    }

    /// Smallest distance from q to any street/road (and lanes/water if asked).
    float Clearance(Vector3 q, bool lanesToo = true, bool waterToo = true)
    {
        float best = float.MaxValue;
        foreach (var t in stone) best = Mathf.Min(best, DistToTrack(t, q));
        foreach (var t in roads) best = Mathf.Min(best, DistToTrack(t, q));
        if (lanesToo) foreach (var t in lanes) best = Mathf.Min(best, DistToTrack(t, q));
        if (waterToo) foreach (var t in water) best = Mathf.Min(best, DistToTrack(t, q));
        return best;
    }

    static Vector3 ClosestOnTracks(List<Track> tracks, Vector3 q)
    {
        float best = float.MaxValue;
        Vector3 result = q;
        foreach (var t in tracks)
            for (int i = 0; i < t.pts.Count - 1; i++)
            {
                Vector3 c = Vector3.Lerp(t.pts[i], t.pts[i + 1], ProjectT(t.pts[i], t.pts[i + 1], q));
                float d = Flat(q - c).magnitude;
                if (d < best) { best = d; result = c; }
            }
        return result;
    }

    static float ProjectT(Vector3 a, Vector3 b, Vector3 q)
    {
        Vector3 ab = Flat(b - a);
        float len2 = ab.sqrMagnitude;
        return len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(Flat(q - a), ab) / len2);
    }

    static float TrackLength(Track t)
    {
        float len = 0f;
        for (int i = 1; i < t.pts.Count; i++) len += Flat(t.pts[i] - t.pts[i - 1]).magnitude;
        return len;
    }

    static float ArcLengthAt(Track t, int index)
    {
        float len = 0f;
        for (int i = 1; i <= index && i < t.pts.Count; i++) len += Flat(t.pts[i] - t.pts[i - 1]).magnitude;
        return len;
    }

    static void PointAt(Track t, float s, out Vector3 pos, out Vector3 tan, out float halfW)
    {
        for (int i = 0; i < t.pts.Count - 1; i++)
        {
            float seg = Flat(t.pts[i + 1] - t.pts[i]).magnitude;
            if (s <= seg || i == t.pts.Count - 2)
            {
                float u = seg > 0f ? Mathf.Clamp01(s / seg) : 0f;
                pos = Vector3.Lerp(t.pts[i], t.pts[i + 1], u);
                tan = Flat(t.pts[i + 1] - t.pts[i]).normalized;
                halfW = Mathf.Lerp(t.widths[i], t.widths[i + 1], u) * 0.5f;
                return;
            }
            s -= seg;
        }
        pos = t.pts[0];
        tan = Vector3.forward;
        halfW = t.widths[0] * 0.5f;
    }

    static Vector3 PointAtFrac(Track t, float f)
    {
        PointAt(t, TrackLength(t) * f, out Vector3 p, out _, out _);
        return p;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// Smooth curve through all control points (Catmull-Rom), sampled every ~'step' meters.
    static List<Vector3> SampleSpline(List<Vector3> ctrl, float step, bool closed)
    {
        var result = new List<Vector3>();
        int n = ctrl.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            Vector3 p0 = closed ? ctrl[(i - 1 + n) % n] : ctrl[Mathf.Max(i - 1, 0)];
            Vector3 p1 = ctrl[i];
            Vector3 p2 = ctrl[(i + 1) % n];
            Vector3 p3 = closed ? ctrl[(i + 2) % n] : ctrl[Mathf.Min(i + 2, n - 1)];
            int steps = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(p1, p2) / step));
            for (int s = 0; s < steps; s++) result.Add(CatmullRom(p0, p1, p2, p3, s / (float)steps));
        }
        if (!closed) result.Add(ctrl[n - 1]);
        return result;
    }

    static bool RaySegment(Vector2 d, Vector2 a, Vector2 b, out float t)
    {
        // Ray from the origin along d, against segment a-b.
        Vector2 e = b - a;
        float den = d.x * e.y - d.y * e.x;
        t = 0f;
        if (Mathf.Abs(den) < 1e-6f) return false;
        t = (a.x * e.y - a.y * e.x) / den;
        float u = (a.x * d.y - a.y * d.x) / den;
        return t >= 0f && u >= 0f && u <= 1f;
    }

    static bool SegmentsCross(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 hit)
    {
        Vector2 p = new Vector2(a.x, a.z), r = new Vector2(b.x - a.x, b.z - a.z);
        Vector2 q = new Vector2(c.x, c.z), s = new Vector2(d.x - c.x, d.z - c.z);
        float den = r.x * s.y - r.y * s.x;
        hit = Vector3.zero;
        if (Mathf.Abs(den) < 1e-6f) return false;
        Vector2 qp = q - p;
        float t = (qp.x * s.y - qp.y * s.x) / den;
        float u = (qp.x * r.y - qp.y * r.x) / den;
        if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
        hit = new Vector3(p.x + r.x * t, 0f, p.y + r.y * t);
        return true;
    }

    // =========================================================================================
    // Small math/random helpers
    // =========================================================================================

    float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
    bool Chance(float p) => rng.NextDouble() < p;
    T Pick<T>(IList<T> list) => list[rng.Next(list.Count)];
    Quaternion Yaw() => Quaternion.Euler(0f, Range(0f, 360f), 0f);

    Vector3 RandomInCircle(float r)
    {
        float a = Range(0f, Mathf.PI * 2f), d = Mathf.Sqrt((float)rng.NextDouble()) * r;
        return Dir(a) * d;
    }

    List<Vector3> JitterRing(Vector3 c, float r, int n) =>
        Enumerable.Range(0, n).Select(i => c + Dir(i / (float)n * Mathf.PI * 2f) * r * Range(0.8f, 1.15f)).ToList();

    static Vector3 Dir(float rad) => new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
    static Vector3 Perp(float rad) => new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
    static Vector3 RightOf(Vector3 fwd) => new Vector3(fwd.z, 0f, -fwd.x);
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // =========================================================================================
    // MeshBuilder: collects vertices/triangles, then makes a Unity Mesh.
    // A mesh is just: a list of points (vertices), a list of triangles (3 indices into that list
    // each), and UVs (where each vertex samples the texture).
    // =========================================================================================

    class MeshBuilder
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<int> tris = new List<int>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly float uvTile;

        public MeshBuilder(float uvTileMeters) { uvTile = uvTileMeters; }
        public bool IsEmpty => tris.Count == 0;

        // World-space UVs: the texture repeats every 'uvTile' meters everywhere, so stones are the
        // same size on every street and on the plaza.
        int Add(Vector3 p)
        {
            verts.Add(p);
            uvs.Add(new Vector2(p.x, p.z) / uvTile);
            return verts.Count - 1;
        }

        /// A strip that follows a polyline. 'keep' can cut holes (e.g. curbs at street mouths).
        public void AddRibbon(List<Vector3> pts, List<float> widths, float y, bool closed = false, Func<Vector3, bool> keep = null)
        {
            int n = pts.Count;
            if (n < 2) return;
            var left = new int[n];
            var right = new int[n];
            var ok = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 prev = i > 0 ? pts[i - 1] : (closed ? pts[n - 1] : pts[i]);
                Vector3 next = i < n - 1 ? pts[i + 1] : (closed ? pts[0] : pts[i]);
                Vector3 tan = next - prev;
                tan.y = 0f;
                tan.Normalize();
                Vector3 r = new Vector3(tan.z, 0f, -tan.x);
                Vector3 p = new Vector3(pts[i].x, y, pts[i].z);
                float hw = widths[i] * 0.5f;
                ok[i] = keep == null || keep(p);
                left[i] = Add(p - r * hw);
                right[i] = Add(p + r * hw);
            }
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n;
                if (!ok[i] || !ok[j]) continue;
                // Clockwise seen from above => the face points up (Unity's front-face rule).
                tris.Add(left[i]); tris.Add(left[j]); tris.Add(right[i]);
                tris.Add(right[i]); tris.Add(left[j]); tris.Add(right[j]);
            }
        }

        /// A filled polygon made of triangles around a center (plaza, patches).
        /// The ring must go counter-clockwise by angle (x = cos, z = sin).
        public void AddFan(Vector3 center, List<Vector3> ring, float y)
        {
            int c = Add(new Vector3(center.x, y, center.z));
            int first = verts.Count;
            foreach (var p in ring) Add(new Vector3(p.x, y, p.z));
            for (int i = 0; i < ring.Count; i++)
            {
                int a = first + i, b = first + (i + 1) % ring.Count;
                tris.Add(c); tris.Add(b); tris.Add(a);
            }
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents(); // needed by the cobblestone normal map
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
