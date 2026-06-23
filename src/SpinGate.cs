using System.Collections.Generic;
using UnityEngine;

// SPIN GATE — a one-tap 3D "thread the gap" knife game (Knife-Hit family, but a fresh core).
// A neon GATE ring spins around a glowing CORE crystal. The ONLY control is THROW (tap / click / Space / Up):
// a knife flies up from below toward the centre. It only reaches the core if a GAP in the spinning ring is
// passing the bottom at that instant — thread it CLEAN and the blade strikes the core for a crack. Hit the
// solid wall and the knife SHATTERS: GAME OVER. Land it dead-centre for a PERFECT, shave the wall edge for a
// risky CLOSE bonus. Crack the core enough times and it SHATTERS — the gate reforms faster, narrower, and at
// boss stages a second concentric ring guards it. Tense in five seconds, juicy on every thunk.
//
// Differentiated from blade-circus (stick a knife into a wheel face and avoid your own stuck knives): here you
// THREAD a flying knife through a moving GAP — the threat is the rotating wall, not your blades — and the prize
// is breaking the core behind the gate. A single rotating gap keeps an opening at the bottom frequently, so it
// is always solvable; difficulty comes from speed, gap width, oscillation/reversals, and the boss's 2nd ring.
//
// Built entirely in code (CreatePrimitive + procedural placement) so it renders reliably in WebGL with engine
// stripping disabled. NO Rigidbody/colliders: the ring is pure Transform rotation and every hit is an angular
// test at the bottom contact in each ring's own rotating frame. Coexists with the permanent Juice & AutoShot.
public class SpinGate : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__SpinGate");
        go.AddComponent<SpinGate>();
        DontDestroyOnLoad(go);
    }

    // ---------------------------------------------------------------- tuning
    static readonly Vector3 CORE = new Vector3(0f, 1.75f, 0f);
    const float CORE_R = 0.52f;            // core crystal radius (knife "lands" when its tip reaches this)
    const float LAUNCH_GAP = 1.7f;         // how far below the outer ring the knife tip starts
    const float FLY_SPEED = 12.5f;         // knife flight speed (units/s)
    const float KNIFE_HALFW = 0.105f;      // knife half-width (world) -> angular half at each ring radius
    const float KNIFE_Z = -0.18f;          // knife plane (slightly in front of the rings at z=0)
    const float PERF_FRAC = 0.20f;         // |gap-centre offset| < gapHalf*this => PERFECT
    const float CLOSE_BAND = 6.0f;         // within this many deg of the wall (but clean) => CLOSE

    // ---------------------------------------------------------------- scene refs
    Transform camT; Camera camComp;
    Transform flyKnifeT;                   // single in-flight / ready knife (world space)
    Transform readyHintT;                  // glow marking the throw lane
    Transform coreT; Renderer coreRend; Material coreMat;
    Transform coreGlowT;
    TextMesh hudStage, hudScore, hudBest, hudCore, comboText, bannerText, dbg;

    Material steelMat, handleMat, boltMat, ringMatA, ringMatB, postMat, coreBaseMat, bgMat;

    // ---------------------------------------------------------------- ring model
    class Ring
    {
        public Transform t;                // rotating parent (spins about Z)
        public float radius;
        public float baseSpeed, amp, freq, dir;   // angVel = dir*(baseSpeed + amp*sin(time*freq))
        public float angle;                // current rotation (deg)
        public float[] gapCenters;         // local gap centres (deg)
        public float gapHalf;              // half-width of each gap (deg)
        public bool passed;                // already evaluated for the current shot
        public Material mat;
        public readonly List<GameObject> parts = new List<GameObject>();
    }
    readonly List<Ring> rings = new List<Ring>();

    // ---------------------------------------------------------------- run state
    enum State { Playing, Shatter, Dead }
    State state = State.Playing;
    enum Throw { Ready, Flying }
    Throw thr = Throw.Ready;

    int stage = 1;
    int coreHp, coreHpMax;
    int score, best, combo, bestCombo;
    bool attract = true;                   // auto-demo until first real input
    bool showDbg, isBoss;

    float startTipY, tipY;                 // knife tip Y (root pivot is the tip)
    float shatterTimer, deathTimer, comboFlash;
    float deathVy, deathSpin, deathVx;
    float gTime;                           // gameplay clock driving ring oscillation
    float coreKick;                        // decaying squash kick when the core is struck

    // last-shot telemetry (debug / popups)
    float lastGd, lastSlack;

    // HUD layout (aspect-adaptive)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);
        // purge any stray default mesh objects baked into the built scene
        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            if (mf.gameObject.name == "Cube" || mf.gameObject.name == "Sphere") Destroy(mf.gameObject);

        best = PlayerPrefs.GetInt("spingate_best", 0);
        bestCombo = PlayerPrefs.GetInt("spingate_bestcombo", 0);

        BuildMaterials();
        BuildEnvironment();
        BuildCamera();
        BuildCore();
        BuildKnife();
        BuildHud();

        stage = 1; score = 0; combo = 0;
        NewStage(true);
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.3f, bool emissive = false, float emi = 0.8f, float alpha = 1f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        c.a = alpha;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emi); }
        if (alpha < 1f) SetTransparent(m, c);
        return m;
    }

    static void SetTransparent(Material m, Color c)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    void BuildMaterials()
    {
        steelMat   = Mat(new Color(0.88f, 0.92f, 0.98f), 0.25f, 0.65f);
        handleMat  = Mat(new Color(0.92f, 0.36f, 0.30f), 0.10f, 0.45f);
        boltMat    = Mat(new Color(0.97f, 0.84f, 0.40f), 0.7f, 0.8f, true, 0.5f);
        ringMatA   = Mat(new Color(0.20f, 0.85f, 0.95f), 0.20f, 0.6f, true, 0.85f);   // cyan gate
        ringMatB   = Mat(new Color(0.70f, 0.40f, 1.00f), 0.20f, 0.6f, true, 0.85f);   // violet inner gate
        postMat    = Mat(new Color(1.00f, 0.30f, 0.62f), 0.15f, 0.7f, true, 1.6f);    // hot pink gap posts
        coreBaseMat= Mat(new Color(0.30f, 0.95f, 0.80f), 0.10f, 0.7f, true, 1.4f);    // core crystal
        bgMat      = Mat(new Color(0.06f, 0.07f, 0.13f), 0f, 0.05f);
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Material shared)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared;
        return g;
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.85f, 0.92f, 1f);
        sun.intensity = 1.05f;
        sun.transform.rotation = Quaternion.Euler(40f, -18f, 0f);
        sun.shadows = LightShadows.None;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.20f, 0.26f, 0.40f);
        RenderSettings.ambientEquatorColor = new Color(0.14f, 0.16f, 0.26f);
        RenderSettings.ambientGroundColor = new Color(0.05f, 0.06f, 0.10f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.05f, 0.06f, 0.12f);
        RenderSettings.fogStartDistance = 14f;
        RenderSettings.fogEndDistance = 46f;

        var back = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(80f, 52f, 0.6f), bgMat);
        back.transform.position = new Vector3(0, 4f, 9f);

        // concentric faint guide rings in the background for depth
        for (int i = 0; i < 3; i++)
        {
            float rr = 4.2f + i * 1.6f;
            var gm = Mat(new Color(0.16f, 0.22f, 0.42f, 0.5f), 0f, 0.3f, true, 0.4f, 0.5f);
            int n = 48;
            var holder = new GameObject("guide" + i).transform;
            holder.position = new Vector3(CORE.x, CORE.y, 5.5f);
            for (int k = 0; k < n; k++)
            {
                float a = k * 360f / n;
                var s = Prim(PrimitiveType.Cube, holder, new Vector3(Mathf.Cos(a * Mathf.Deg2Rad) * rr, Mathf.Sin(a * Mathf.Deg2Rad) * rr, 0f),
                    new Vector3(0.08f, 0.5f, 0.08f), gm);
                s.transform.localRotation = Quaternion.Euler(0, 0, a);
            }
        }

    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.04f, 0.05f, 0.10f);
        camComp.fieldOfView = 46f;
        camComp.farClipPlane = 120f;
        cgo.AddComponent<AudioListener>();
        camT = cgo.transform;
        camT.rotation = Quaternion.Euler(1.5f, 0f, 0f);
        UpdateCameraRig();
    }

    void UpdateCameraRig()
    {
        if (camComp == null || camT == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float halfVtan = Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        const float TARGET_HALF_W = 3.35f;     // keep the whole gate + a bit of the throw lane on screen
        float dist = TARGET_HALF_W / Mathf.Max(0.05f, halfVtan * aspect);
        dist = Mathf.Clamp(dist, 9.0f, 15.5f);
        camT.position = new Vector3(0f, CORE.y - 0.35f, -dist);
    }

    // ===================================================================== core crystal
    void BuildCore()
    {
        coreT = new GameObject("Core").transform;
        coreT.position = CORE;
        coreMat = new Material(coreBaseMat);
        var body = Prim(PrimitiveType.Sphere, coreT, Vector3.zero, Vector3.one * (CORE_R * 2f), coreMat);
        coreRend = body.GetComponent<Renderer>();
        coreRend.sharedMaterial = coreMat;
        // crystal facets
        for (int i = 0; i < 3; i++)
        {
            var f = Prim(PrimitiveType.Cube, coreT, Vector3.zero, new Vector3(CORE_R * 2.05f, 0.06f, CORE_R * 2.05f), coreMat);
            f.transform.localRotation = Quaternion.Euler(0, 0, i * 60f);
        }
        // inner bright nucleus
        var glow = Prim(PrimitiveType.Sphere, coreT, Vector3.zero, Vector3.one * (CORE_R * 1.1f),
            Mat(new Color(0.85f, 1f, 0.95f), 0f, 0.9f, true, 2.2f));
        coreGlowT = glow.transform;
    }

    void SetCoreLook()
    {
        // tint from teal (full) -> hot orange/white (nearly broken)
        float dmg = coreHpMax > 0 ? 1f - (float)coreHp / coreHpMax : 0f;
        Color full = new Color(0.30f, 0.95f, 0.80f);
        Color low = new Color(1.0f, 0.55f, 0.25f);
        Color c = Color.Lerp(full, low, dmg);
        if (coreMat.HasProperty("_BaseColor")) coreMat.SetColor("_BaseColor", c);
        if (coreMat.HasProperty("_Color")) coreMat.SetColor("_Color", c);
        if (coreMat.HasProperty("_EmissionColor")) coreMat.SetColor("_EmissionColor", c * (1.4f + dmg * 1.2f));
    }

    // ===================================================================== knife
    void BuildKnife()
    {
        var root = new GameObject("FlyKnife").transform;   // root pivot = the TIP (top of the knife)
        Prim(PrimitiveType.Cube, root, new Vector3(0, -0.12f, 0), new Vector3(0.05f, 0.26f, 0.05f), steelMat);      // tip
        Prim(PrimitiveType.Cube, root, new Vector3(0, -0.62f, 0), new Vector3(0.15f, 0.78f, 0.09f), steelMat);      // blade
        Prim(PrimitiveType.Cube, root, new Vector3(0, -1.06f, 0), new Vector3(0.34f, 0.10f, 0.16f), boltMat);       // guard
        Prim(PrimitiveType.Cube, root, new Vector3(0, -1.52f, 0), new Vector3(0.19f, 0.82f, 0.19f), handleMat);     // handle
        Prim(PrimitiveType.Sphere, root, new Vector3(0, -1.98f, 0), Vector3.one * 0.21f, boltMat);                  // pommel
        flyKnifeT = root;

        readyHintT = Prim(PrimitiveType.Quad, null, Vector3.zero, new Vector3(0.55f, 2.0f, 1f),
            Mat(new Color(0.3f, 0.9f, 1f, 0.18f), 0f, 0.2f, true, 0.6f, 0.18f)).transform;
    }

    // ===================================================================== ring construction
    Ring BuildRing(float radius, float[] gapCenters, float gapHalf, float baseSpeed, float amp, float freq, float dir, Material mat)
    {
        var r = new Ring
        {
            radius = radius, gapCenters = gapCenters, gapHalf = gapHalf,
            baseSpeed = baseSpeed, amp = amp, freq = freq, dir = dir, mat = mat, angle = 0f
        };
        r.t = new GameObject("Gate").transform;
        r.t.position = CORE;

        int step = 5;
        for (int a = 0; a < 360; a += step)
        {
            if (InAnyGap(a + step * 0.5f, gapCenters, gapHalf)) continue;
            float rad = (a + step * 0.5f) * Mathf.Deg2Rad;
            float tang = radius * (step * Mathf.Deg2Rad) * 1.10f;   // chord length to overlap neighbours
            var seg = Prim(PrimitiveType.Cube, r.t,
                new Vector3(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius, 0f),
                new Vector3(0.26f, tang, 0.42f), mat);
            seg.transform.localRotation = Quaternion.Euler(0, 0, a + step * 0.5f);
            r.parts.Add(seg);
        }
        // bright posts at each gap edge so the opening is unmistakable
        foreach (float gc in gapCenters)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                float edge = (gc + s * gapHalf) * Mathf.Deg2Rad;
                var p = Prim(PrimitiveType.Sphere, r.t, new Vector3(Mathf.Cos(edge) * radius, Mathf.Sin(edge) * radius, 0f),
                    Vector3.one * 0.30f, postMat);
                r.parts.Add(p);
            }
        }
        return r;
    }

    static bool InAnyGap(float a, float[] centers, float half)
    {
        foreach (float c in centers) if (AngDiff(a, c) < half) return true;
        return false;
    }

    void ClearRings()
    {
        foreach (var r in rings) if (r.t != null) Destroy(r.t.gameObject);
        rings.Clear();
    }

    // ===================================================================== stage lifecycle
    void NewStage(bool first)
    {
        state = State.Playing;
        thr = Throw.Ready;
        shatterTimer = 0f; deathTimer = 0f;
        hudStage.gameObject.SetActive(true);
        hudScore.gameObject.SetActive(true);
        hudCore.gameObject.SetActive(true);
        hudBest.gameObject.SetActive(true);

        ClearRings();
        isBoss = (stage % 5 == 0);

        float t = Mathf.Clamp01((stage - 1) / 14f);
        float baseSpeed = Mathf.Lerp(58f, 132f, t);
        float gapHalf = Mathf.Lerp(46f, 27f, t);
        float amp = stage >= 4 ? Mathf.Lerp(0f, 70f, Mathf.Clamp01((stage - 3) / 10f)) : 0f;
        float freq = 0.65f + 0.05f * (stage % 3);
        float dir = (stage % 2 == 0) ? -1f : 1f;

        // occasional 2-gap "breather" variation (more openings, but spun faster)
        bool twoGap = (!isBoss && stage >= 6 && stage % 3 == 0);
        float[] gc0 = twoGap ? new float[] { 90f, 270f } : new float[] { 270f };
        if (twoGap) { baseSpeed *= 1.25f; gapHalf = Mathf.Max(gapHalf, 30f); }

        coreHpMax = isBoss ? 8 : Mathf.Min(4 + stage / 2, 8);
        coreHp = coreHpMax;
        SetCoreLook();
        coreT.gameObject.SetActive(true);

        if (isBoss)
        {
            // two concentric rings; harmonic (geared) speeds + shared gap phase keep windows frequent & rhythmic
            float bs = Mathf.Lerp(70f, 118f, t);
            rings.Add(BuildRing(2.75f, new float[] { 270f }, Mathf.Max(gapHalf, 33f), bs, amp, freq, 1f, ringMatA));
            rings.Add(BuildRing(1.78f, new float[] { 270f }, Mathf.Max(gapHalf + 4f, 36f), bs * 0.5f, amp * 0.6f, freq, -1f, ringMatB));
        }
        else
        {
            rings.Add(BuildRing(2.35f, gc0, gapHalf, baseSpeed, amp, freq, dir, ringMatA));
        }

        // load the ready knife
        float outerR = rings[0].radius;
        startTipY = CORE.y - (outerR + LAUNCH_GAP);
        tipY = startTipY;
        flyKnifeT.gameObject.SetActive(true);
        flyKnifeT.rotation = Quaternion.identity;
        foreach (var r in rings) r.passed = false;
        PlaceFlyKnife();

        RefreshHud();
        Banner(isBoss ? "BOSS GATE " + stage : "STAGE " + stage,
               isBoss ? new Color(0.85f, 0.7f, 1f) : new Color(0.6f, 0.95f, 1f), 1.0f);
    }

    // ===================================================================== input
    bool ThrowPressed()
    {
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) return true;
        if (Input.GetMouseButtonDown(0)) return true;
        for (int i = 0; i < Input.touchCount; i++)
            if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
        return false;
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;
        gTime += dt;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        bool pressed = ThrowPressed();
        if (pressed && attract && state == State.Playing) { attract = false; pressed = false; }   // first tap just wakes it

        // spin the rings
        foreach (var r in rings)
        {
            float av = r.dir * (r.baseSpeed + r.amp * Mathf.Sin(gTime * r.freq));
            r.angle += av * dt;
            r.t.localRotation = Quaternion.Euler(0, 0, r.angle);
        }

        // breathing core (+ decaying squash kick on hits)
        if (coreKick > 0f) coreKick = Mathf.Lerp(coreKick, 0f, dt * 9f);
        if (coreT != null && coreT.gameObject.activeSelf)
        {
            float p = 1f + Mathf.Sin(gTime * 3.2f) * 0.05f + coreKick;
            coreT.localScale = Vector3.one * p;
            if (coreGlowT != null) coreGlowT.localRotation = Quaternion.Euler(0, 0, gTime * 60f);
        }

        switch (state)
        {
            case State.Playing: TickPlaying(dt, pressed); break;
            case State.Shatter: TickShatter(dt); break;
            case State.Dead:    TickDead(dt, pressed); break;
        }

        if (comboFlash > 0f) comboFlash -= dt * 2.2f;
        TickBanner(dt);
        UpdateCameraRig();
        AdjustHud();
        if (showDbg) UpdateDbg();
    }

    void TickPlaying(float dt, bool pressed)
    {
        bool fire = pressed;
        if (attract && thr == Throw.Ready) fire = AutoShouldThrow();

        if (thr == Throw.Ready)
        {
            tipY = startTipY + Mathf.Sin(Time.time * 4f) * 0.06f;
            PlaceFlyKnife();
            readyHintT.gameObject.SetActive(true);
            readyHintT.position = new Vector3(0f, startTipY - 0.3f, KNIFE_Z + 0.05f);
            float pulse = 1f + Mathf.Sin(Time.time * 6f) * 0.12f;
            readyHintT.localScale = new Vector3(0.5f, 2.0f * pulse, 1f);
            if (fire) { thr = Throw.Flying; foreach (var r in rings) r.passed = false; Juice.Blip(540f, 0.05f, 0.3f); }
        }
        else // Flying
        {
            readyHintT.gameObject.SetActive(false);
            tipY += FLY_SPEED * dt;
            PlaceFlyKnife();

            float tipRadius = CORE.y - tipY;     // distance from core centre while below it

            // evaluate ring crossings, outer (largest radius) first
            for (int i = 0; i < rings.Count; i++)
            {
                var r = rings[i];
                if (r.passed) continue;
                if (tipRadius <= r.radius)
                {
                    r.passed = true;
                    float contact = Norm(270f - r.angle);
                    float gd = GapDist(contact, r.gapCenters);
                    float knifeAng = Mathf.Asin(Mathf.Clamp01(KNIFE_HALFW / r.radius)) * Mathf.Rad2Deg;
                    float clear = r.gapHalf - knifeAng;
                    lastGd = gd; lastSlack = clear - gd;
                    if (gd > clear) { HitWall(r); return; }
                }
            }

            if (tipRadius <= CORE_R) { HitCore(); return; }
        }
    }

    void PlaceFlyKnife()
    {
        flyKnifeT.position = new Vector3(0f, tipY, KNIFE_Z);
        flyKnifeT.rotation = Quaternion.identity;
    }

    void HitCore()
    {
        coreHp--;
        combo++;
        if (combo > bestCombo) { bestCombo = combo; PlayerPrefs.SetInt("spingate_bestcombo", bestCombo); }

        // scoring: base + perfect/close, scaled by combo
        int gain = 10;
        string tag = "";
        Color popCol = new Color(0.4f, 1f, 0.85f);
        float perfWin = rings.Count > 0 ? rings[0].gapHalf * PERF_FRAC : 6f;
        if (lastGd <= perfWin) { gain += 15; tag = "PERFECT!"; popCol = new Color(1f, 0.95f, 0.4f); Juice.Shake(0.16f); }
        else if (lastSlack <= CLOSE_BAND) { gain += 10; tag = "CLOSE!"; popCol = new Color(1f, 0.5f, 0.6f); }
        float mult = 1f + Mathf.Min(combo, 30) * 0.12f;
        int add = Mathf.RoundToInt(gain * mult);
        score += add;
        if (score > best) { best = score; PlayerPrefs.SetInt("spingate_best", best); }

        // feedback
        Vector3 cpos = coreT.position;
        Juice.Score(cpos);
        Juice.Pop(cpos, popCol, 12);
        Juice.Blip(540f + Mathf.Min(combo, 16) * 45f, 0.07f, 0.4f);
        Juice.Shake(0.10f);
        SetCoreLook();
        StartCoroutineSafe();

        comboText.text = (tag.Length > 0 ? tag + "  " : "") + (combo > 1 ? "x" + combo + "  " : "") + "+" + add;
        comboFlash = 1f;

        RefreshHud();

        if (coreHp <= 0) { CoreShatter(); return; }

        // reload
        thr = Throw.Ready;
        tipY = startTipY;
        flyKnifeT.gameObject.SetActive(true);
        flyKnifeT.rotation = Quaternion.identity;
        PlaceFlyKnife();
    }

    // tiny squash kick on the core when struck (applied/decayed in Update's breathing block)
    void StartCoroutineSafe()
    {
        coreKick = 0.22f;
    }

    void CoreShatter()
    {
        state = State.Shatter;
        shatterTimer = 0f;
        flyKnifeT.gameObject.SetActive(false);
        readyHintT.gameObject.SetActive(false);
        int bonus = 40 + stage * 12;
        score += bonus;
        if (score > best) { best = score; PlayerPrefs.SetInt("spingate_best", best); PlayerPrefs.Save(); }

        Vector3 cpos = coreT.position;
        Juice.Pop(cpos, new Color(0.5f, 1f, 0.9f), 18);
        Juice.Pop(cpos, new Color(1f, 0.9f, 0.5f), 14);
        Juice.Lose();   // big low thud + shake reused for the satisfying break
        Juice.Blip(680f, 0.1f, 0.4f); Juice.Blip(900f, 0.1f, 0.4f); Juice.Blip(1200f, 0.12f, 0.4f);
        Juice.Shake(0.35f);
        coreT.gameObject.SetActive(false);

        Banner("GATE BROKEN!   +" + bonus, new Color(0.6f, 1f, 0.75f), 1.2f);
        RefreshHud();
    }

    void TickShatter(float dt)
    {
        shatterTimer += dt;
        if (shatterTimer >= 1.15f) { stage++; NewStage(false); }
    }

    void HitWall(Ring r)
    {
        if (state == State.Dead) return;
        state = State.Dead;
        deathTimer = 0f;
        deathVy = 4.5f; deathVx = (Random.value < 0.5f ? -1f : 1f) * 1.6f;
        deathSpin = (Random.value < 0.5f) ? 620f : -620f;

        // stop the knife at the wall it struck
        tipY = CORE.y - r.radius;
        PlaceFlyKnife();

        combo = 0;
        Juice.Lose();
        Vector3 hit = new Vector3(0f, tipY, KNIFE_Z);
        Juice.Pop(hit, new Color(0.9f, 0.95f, 1f), 14);
        Juice.Pop(hit, r.mat.GetColor(r.mat.HasProperty("_BaseColor") ? "_BaseColor" : "_Color"), 10);
        Juice.Blip(130f, 0.18f, 0.5f);
        Juice.Shake(0.3f);

        if (score > best) best = score;
        PlayerPrefs.SetInt("spingate_best", Mathf.Max(best, PlayerPrefs.GetInt("spingate_best", 0)));
        PlayerPrefs.Save();

        Banner("GAME OVER\nSCORE " + score + "    BEST " + best + "\nTAP / R to retry", Color.white, 999f, 0.82f);
        comboText.text = "";
        hudStage.gameObject.SetActive(false);
        hudScore.gameObject.SetActive(false);
        hudCore.gameObject.SetActive(false);
        hudBest.gameObject.SetActive(false);
        RefreshHud();
    }

    void TickDead(float dt, bool pressed)
    {
        deathTimer += dt;
        deathVy -= 22f * dt;
        tipY += deathVy * dt;
        flyKnifeT.position = new Vector3(flyKnifeT.position.x + deathVx * dt, tipY, KNIFE_Z);
        flyKnifeT.Rotate(0, 0, deathSpin * dt, Space.Self);

        if (deathTimer > 0.4f && (Input.GetKeyDown(KeyCode.R) || pressed))
        {
            stage = 1; score = 0; combo = 0;
            flyKnifeT.rotation = Quaternion.identity;
            NewStage(true);
            attract = false;   // player retried — hand them control immediately
        }
    }

    // ===================================================================== auto-demo brain (only throws on a safe window => never dies)
    float autoCooldown;
    bool AutoShouldThrow()
    {
        autoCooldown -= Time.deltaTime;
        if (autoCooldown > 0f) return false;
        float slack;
        if (PredictSafe(6f, out slack))
        {
            autoCooldown = Random.Range(0.3f, 0.6f);
            return true;
        }
        return false;
    }

    // would a throw fired NOW clear every ring with at least `margin` deg of slack?
    bool PredictSafe(float margin, out float minSlack)
    {
        minSlack = 999f;
        float launchR = (rings.Count > 0 ? rings[0].radius : 2.4f) + LAUNCH_GAP;
        foreach (var r in rings)
        {
            float flight = Mathf.Max(0f, (launchR - r.radius)) / FLY_SPEED;
            float av = r.dir * (r.baseSpeed + r.amp * Mathf.Sin(gTime * r.freq));
            float predAngle = r.angle + av * flight;
            float contact = Norm(270f - predAngle);
            float gd = GapDist(contact, r.gapCenters);
            float knifeAng = Mathf.Asin(Mathf.Clamp01(KNIFE_HALFW / r.radius)) * Mathf.Rad2Deg;
            float clear = r.gapHalf - knifeAng;
            float slack = clear - gd;
            minSlack = Mathf.Min(minSlack, slack);
        }
        return minSlack >= margin;
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(camT, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudStage  = MakeText(0.072f, Color.white, TextAnchor.UpperLeft);
        hudScore  = MakeText(0.052f, new Color(0.55f, 0.95f, 1f), TextAnchor.UpperLeft);
        hudBest   = MakeText(0.052f, new Color(0.8f, 0.9f, 1f), TextAnchor.UpperRight);
        hudCore   = MakeText(0.058f, new Color(0.6f, 1f, 0.85f), TextAnchor.LowerCenter);
        comboText = MakeText(0.075f, new Color(1f, 0.85f, 0.4f), TextAnchor.MiddleCenter);
        bannerText= MakeText(0.11f, Color.white, TextAnchor.MiddleCenter);
        dbg       = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 5.4f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.2f, 1.3f);
        float ix = halfW * 0.93f, iy = halfH * 0.93f;

        hudStage.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudStage.characterSize = 0.072f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.62f * hudScale, HUD_Z); hudScore.characterSize = 0.052f * hudScale;
        hudBest.transform.localPosition = new Vector3(ix, iy, HUD_Z); hudBest.characterSize = 0.052f * hudScale;
        hudCore.transform.localPosition = new Vector3(0, -iy, HUD_Z); hudCore.characterSize = 0.058f * hudScale;
        comboText.transform.localPosition = new Vector3(0, -halfH * 0.42f, HUD_Z);   // lower third, clear of the gate/core
        if (comboFlash <= 0f) comboText.characterSize = 0.055f * hudScale;
        else comboText.characterSize = 0.055f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.4f);
        dbg.transform.localPosition = new Vector3(-ix, -iy * 0.35f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
    }

    void RefreshHud()
    {
        if (hudStage) hudStage.text = (isBoss ? "BOSS " : "STAGE ") + stage;
        if (hudScore) hudScore.text = "SCORE " + score + (combo > 1 ? "    x" + combo : "");
        if (hudBest) hudBest.text = "BEST " + best + (bestCombo > 1 ? "\nMAX x" + bestCombo : "");
        if (hudCore)
        {
            int left = Mathf.Max(0, coreHp);
            string s = "";
            for (int i = 0; i < left; i++) s += "*";
            hudCore.text = "CORE  " + left + "/" + coreHpMax + "\n" + s;
        }
    }

    // ===================================================================== banners
    float bannerTimer;
    void Banner(string s, Color c, float dur, float sizeMul = 1f)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.52f, HUD_Z);
        bannerText.characterSize = 0.085f * hudScale * sizeMul;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void TickBanner(float dt)
    {
        if (bannerTimer > 0f && bannerTimer < 900f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
    }

    // ===================================================================== helpers
    static float Norm(float deg) { deg %= 360f; if (deg < 0f) deg += 360f; return deg; }
    static float AngDiff(float a, float b)
    {
        float d = Mathf.Abs(Norm(a) - Norm(b)) % 360f;
        return d > 180f ? 360f - d : d;
    }
    static float GapDist(float contact, float[] centers)
    {
        float best = 999f;
        foreach (float c in centers) best = Mathf.Min(best, AngDiff(contact, c));
        return best;
    }

    void UpdateDbg()
    {
        float slack;
        bool safe = PredictSafe(0f, out slack);
        string rs = "";
        for (int i = 0; i < rings.Count; i++)
        {
            var r = rings[i];
            float contact = Norm(270f - r.angle);
            rs += string.Format("\nR{0} r{1:0.0} ang{2:0} av~{3:0} gap{4:0} contactGd{5:0.0}",
                i, r.radius, Norm(r.angle), r.dir * r.baseSpeed, r.gapHalf, GapDist(contact, r.gapCenters));
        }
        dbg.text = string.Format(
            "stage {0} boss {1}  state {2}/{3}\nrings {4} safeNow {5} slack {6:0.0}\ntipY {7:0.00} hp {8}/{9} combo {10} score {11}\nlastGd {12:0.0} lastSlack {13:0.0} fps {14:0}{15}",
            stage, isBoss, state, thr, rings.Count, safe, slack,
            tipY, coreHp, coreHpMax, combo, score, lastGd, lastSlack,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), rs);
    }
}
