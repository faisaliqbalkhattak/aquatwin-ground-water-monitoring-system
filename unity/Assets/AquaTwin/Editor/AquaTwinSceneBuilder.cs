using AquaTwin;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AquaTwinEditor
{
    [InitializeOnLoad]
    public static class AquaTwinSceneBuilder
    {
        private const string EnvironmentName = "AquaTwin_Environment";
        private const string MaterialsFolder = "Assets/AquaTwin/Materials";
        private const string PrefabsFolder = "Assets/AquaTwin/Prefabs";
        private static bool importingTmpResources;

        private static readonly Color Navy = Hex("10151A");
        private static readonly Color Panel = Hex("171D23", 0.96f);
        private static readonly Color PanelSecondary = Hex("20272E", 0.96f);
        private static readonly Color Cyan = Hex("58B8AE");
        private static readonly Color Green = Hex("70B88A");
        private static readonly Color Violet = Hex("8999A6");
        private static readonly Color Amber = Hex("C8A85D");
        private static readonly Color MainText = Hex("F1F4F5");
        private static readonly Color SecondaryText = Hex("98A4AB");
        private static readonly Color Divider = Hex("303941");

        static AquaTwinSceneBuilder()
        {
            EditorApplication.delayCall += TryAutoBuild;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpened += OnSceneOpened;
        }

        [MenuItem("AquaTwin/Build Presentation Scene", priority = 1)]
        public static void BuildPresentationScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before building the AquaTwin scene.");
                return;
            }

            if (!EnsureTmpResources())
                return;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            GameObject existing = GameObject.Find(EnvironmentName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            GameObject oldCanvas = GameObject.Find("DashboardCanvas");
            if (oldCanvas != null)
                Object.DestroyImmediate(oldCanvas);

            EnsureFolder("Assets", "AquaTwin");
            EnsureFolder("Assets/AquaTwin", "Materials");
            EnsureFolder("Assets/AquaTwin", "Prefabs");

            Material steelMaterial = CreateLitMaterial(
                "M_WellSteel", Hex("69777F"), 0.62f, 0.42f, false, 0);
            Material groundMaterial = CreateLitMaterial(
                "M_SurfaceGround", Hex("343538"), 0f, 0.22f, false, 0);
            Material waterMaterial = CreateLitMaterial(
                "M_Water", new Color(0.10f, 0.55f, 0.62f, 0.40f), 0f, 0.92f, true, 0);
            Material waterSurfaceMaterial = CreateLitMaterial(
                "M_WaterSurface", new Color(0.46f, 0.84f, 0.88f, 0.82f), 0f, 0.96f, true, 4);
            Material sensorMaterial = CreateLitMaterial(
                "M_SensorHousing", Hex("263038"), 0.35f, 0.42f, false, 0);
            Material markerMaterial = CreateEmissionMaterial("M_NetworkMarker", Cyan, 0.55f);
            Material soilTop = CreateLitMaterial("M_Soil_Top", Hex("5A5147"), 0f, 0.16f, false, 0);
            Material soilSand = CreateLitMaterial("M_Soil_Sand", Hex("746958"), 0f, 0.14f, false, 0);
            Material soilClay = CreateLitMaterial("M_Soil_Clay", Hex("5E5550"), 0f, 0.12f, false, 0);
            Material soilRock = CreateLitMaterial("M_Soil_Rock", Hex("484B4C"), 0f, 0.2f, false, 0);
            Material aquifer = CreateLitMaterial("M_Aquifer", Hex("344F52"), 0f, 0.24f, false, 0);

            GameObject environment = new GameObject(EnvironmentName);
            BuildGround(environment.transform, groundMaterial,
                new[] { soilTop, soilSand, soilClay, aquifer, soilRock });
            GameObject well = BuildWell(environment.transform, steelMaterial, sensorMaterial,
                waterMaterial, waterSurfaceMaterial, markerMaterial);
            BuildSimulatedNetwork(environment.transform, markerMaterial);
            ConfigureCameraAndLights(environment.transform);
            BuildDashboard(well.GetComponent<WellVisualizer>());

            // Mark the revision only after every scene subsystem built successfully.
            GameObject revisionMarker = new GameObject("UX_Revision_5");
            revisionMarker.transform.SetParent(environment.transform, false);

            PrefabUtility.SaveAsPrefabAsset(well, PrefabsFolder + "/Well_DigitalTwin.prefab");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = well;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log("AquaTwin presentation scene created. Press Play and adjust the WellVisualizer sliders on Well_DigitalTwin.");
        }

        private static void TryAutoBuild()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return;

            if (!NeedsProfessionalRebuild())
                return;

            // This migration is intentionally one-shot: only the obsolete generated
            // scene is allowed to stop Play Mode. Normal future script edits will not.
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                return;
            }

            if (scene.name != "SampleScene")
                return;

            BuildPresentationScene();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode && NeedsProfessionalRebuild())
                EditorApplication.delayCall += TryAutoBuild;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (scene.name == "SampleScene" && NeedsProfessionalRebuild())
                EditorApplication.delayCall += TryAutoBuild;
        }

        private static bool NeedsProfessionalRebuild()
        {
            GameObject environment = GameObject.Find(EnvironmentName);
            if (environment == null)
                return true;

            GameObject dashboard = GameObject.Find("DashboardCanvas");
            return environment.transform.Find("UX_Revision_5") == null ||
                   dashboard == null ||
                   dashboard.transform.Find("TelemetryPanel") == null;
        }

        private static bool EnsureTmpResources()
        {
            TMP_Settings tmpSettings = Resources.Load<TMP_Settings>("TMP Settings");
            if (tmpSettings != null && TMP_Settings.defaultFontAsset != null)
                return true;

            if (importingTmpResources)
                return false;

            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Text).Assembly);
            if (package == null)
            {
                Debug.LogError("AquaTwin could not locate the TextMeshPro package resources.");
                return false;
            }

            string packagePath = package.resolvedPath + "/Package Resources/TMP Essential Resources.unitypackage";
            importingTmpResources = true;
            AssetDatabase.importPackageCompleted += OnTmpResourcesImported;
            AssetDatabase.importPackageFailed += OnTmpResourcesImportFailed;
            AssetDatabase.ImportPackage(packagePath, false);
            return false;
        }

        private static void OnTmpResourcesImported(string packageName)
        {
            AssetDatabase.importPackageCompleted -= OnTmpResourcesImported;
            AssetDatabase.importPackageFailed -= OnTmpResourcesImportFailed;
            importingTmpResources = false;
            EditorApplication.delayCall += BuildPresentationScene;
        }

        private static void OnTmpResourcesImportFailed(string packageName, string error)
        {
            AssetDatabase.importPackageCompleted -= OnTmpResourcesImported;
            AssetDatabase.importPackageFailed -= OnTmpResourcesImportFailed;
            importingTmpResources = false;
            Debug.LogError("TextMeshPro resource import failed: " + error);
        }

        private static GameObject BuildWell(
            Transform parent,
            Material steel,
            Material sensor,
            Material water,
            Material waterSurface,
            Material indicator)
        {
            GameObject root = new GameObject("Well_DigitalTwin");
            root.transform.SetParent(parent, false);

            // Compact surface hardware; the monitored bore continues below grade.
            CreatePrimitive("Surface_Seal", PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, 0.05f, 0f), new Vector3(1.12f, 0.055f, 1.12f), sensor);
            CreatePrimitive("Casing_Collar", PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, 0.16f, 0f), new Vector3(0.88f, 0.07f, 0.88f), steel);
            CreatePrimitive("Wellhead_Riser", PrimitiveType.Cylinder, root.transform,
                new Vector3(0f, 0.42f, 0f), new Vector3(0.52f, 0.2f, 0.52f), steel);
            CreatePrimitive("Sensor_Housing", PrimitiveType.Cube, root.transform,
                new Vector3(0f, 0.78f, 0f), new Vector3(0.62f, 0.17f, 0.48f), sensor);
            CreatePrimitive("Sensor_Indicator", PrimitiveType.Cube, root.transform,
                new Vector3(0f, 0.78f, -0.49f), new Vector3(0.16f, 0.025f, 0.015f), indicator);

            // Thin structural casing drawn as a technical wireframe rather than a solid tube.
            GameObject casingFrame = new GameObject("Subsurface_Casing_Frame");
            casingFrame.transform.SetParent(root.transform, false);
            const int casingSegments = 12;
            const float casingRadius = 0.82f;
            for (int i = 0; i < casingSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / casingSegments;
                Vector3 position = new Vector3(
                    Mathf.Cos(angle) * casingRadius,
                    -3.12f,
                    Mathf.Sin(angle) * casingRadius);
                CreatePrimitive("Casing_Vertical_" + i.ToString("00"), PrimitiveType.Cube,
                    casingFrame.transform, position, new Vector3(0.010f, 6.1f, 0.010f), steel);
            }

            const int ringSegments = 16;
            for (int level = 0; level < 4; level++)
            {
                float y = -0.22f - level * 2f;
                for (int i = 0; i < ringSegments; i++)
                {
                    float angle = i * Mathf.PI * 2f / ringSegments;
                    GameObject segment = CreatePrimitive(
                        "Casing_Ring_" + level + "_" + i.ToString("00"), PrimitiveType.Cube,
                        casingFrame.transform,
                        new Vector3(Mathf.Cos(angle) * casingRadius, y, Mathf.Sin(angle) * casingRadius),
                        new Vector3(0.28f, 0.008f, 0.010f), steel);
                    segment.transform.localRotation = Quaternion.Euler(0f, -angle * Mathf.Rad2Deg - 90f, 0f);
                }
            }

            GameObject waterPivot = new GameObject("WaterLevelPivot");
            waterPivot.transform.SetParent(root.transform, false);
            waterPivot.transform.localPosition = new Vector3(0f, -6.18f, 0f);

            GameObject waterObject = CreatePrimitive("WaterVolume", PrimitiveType.Cylinder, waterPivot.transform,
                new Vector3(0f, 2.92f, 0f), new Vector3(0.78f, 2.92f, 0.78f), water);
            Renderer waterRenderer = waterObject.GetComponent<Renderer>();
            waterRenderer.shadowCastingMode = ShadowCastingMode.Off;

            GameObject surfaceDynamics = new GameObject("Water_Surface_Dynamics");
            surfaceDynamics.transform.SetParent(waterPivot.transform, false);
            surfaceDynamics.transform.localPosition = new Vector3(0f, 5.84f, 0f);

            GameObject surfaceObject = CreatePrimitive("Water_Surface", PrimitiveType.Cylinder, surfaceDynamics.transform,
                Vector3.zero, new Vector3(0.80f, 0.018f, 0.80f), waterSurface);
            Renderer surfaceRenderer = surfaceObject.GetComponent<Renderer>();
            surfaceRenderer.shadowCastingMode = ShadowCastingMode.Off;
            List<Renderer> additionalRenderersList = new List<Renderer> { surfaceRenderer };

            // Two incomplete concentric rings give the top a readable meniscus/ripple cue.
            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? 0.48f : 0.70f;
                int segments = 24;
                for (int i = 0; i < segments; i++)
                {
                    if ((i + ring * 3) % 7 == 0)
                        continue;

                    float angle = i * Mathf.PI * 2f / segments;
                    GameObject ripple = CreatePrimitive(
                        "Surface_Ripple_" + ring + "_" + i.ToString("00"), PrimitiveType.Cube,
                        surfaceDynamics.transform,
                        new Vector3(Mathf.Cos(angle) * radius, 0.032f + ring * 0.01f, Mathf.Sin(angle) * radius),
                        new Vector3(ring == 0 ? 0.095f : 0.13f, 0.006f, 0.009f), waterSurface);
                    ripple.transform.localRotation = Quaternion.Euler(0f, -angle * Mathf.Rad2Deg - 90f, 0f);
                    ripple.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                    additionalRenderersList.Add(ripple.GetComponent<Renderer>());
                }
            }

            // Sparse suspended particles provide depth and motion cues without looking decorative.
            Renderer[] particleRenderers = new Renderer[6];
            Vector3[] particlePositions =
            {
                new Vector3(-0.18f, 0.9f, -0.12f), new Vector3(0.22f, 1.65f, 0.08f),
                new Vector3(-0.12f, 2.35f, 0.2f), new Vector3(0.16f, 3.15f, -0.18f),
                new Vector3(-0.24f, 4.05f, 0.04f), new Vector3(0.08f, 4.8f, 0.18f)
            };
            for (int i = 0; i < particlePositions.Length; i++)
            {
                GameObject particle = CreatePrimitive("Water_Particle_" + (i + 1).ToString("00"),
                    PrimitiveType.Sphere, waterPivot.transform, particlePositions[i],
                    Vector3.one * (0.025f + i * 0.002f), waterSurface);
                particle.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                particleRenderers[i] = particle.GetComponent<Renderer>();
                additionalRenderersList.Add(particleRenderers[i]);
            }

            // A restrained depth ruler makes the cutaway read as an instrument, not a game prop.
            GameObject ruler = new GameObject("Depth_Ruler");
            ruler.transform.SetParent(root.transform, false);
            CreatePrimitive("Ruler_Line", PrimitiveType.Cube, ruler.transform,
                new Vector3(1.18f, -3.15f, 0f), new Vector3(0.012f, 6f, 0.012f), steel);
            for (int i = 0; i < 7; i++)
                CreatePrimitive("Depth_Tick_" + i, PrimitiveType.Cube, ruler.transform,
                    new Vector3(1.11f, -0.2f - i, 0f), new Vector3(0.08f, 0.009f, 0.012f), steel);

            WellVisualizer visualizer = root.AddComponent<WellVisualizer>();
            SerializedObject serialized = new SerializedObject(visualizer);
            serialized.FindProperty("waterLevelPivot").objectReferenceValue = waterPivot.transform;
            serialized.FindProperty("waterRenderer").objectReferenceValue = waterRenderer;
            SerializedProperty surfaceTransformProperty = serialized.FindProperty("waterSurfaceTransform");
            if (surfaceTransformProperty != null)
                surfaceTransformProperty.objectReferenceValue = surfaceDynamics.transform;
            SerializedProperty additionalRenderers = serialized.FindProperty("additionalWaterRenderers");
            additionalRenderers.arraySize = additionalRenderersList.Count;
            for (int i = 0; i < additionalRenderersList.Count; i++)
                additionalRenderers.GetArrayElementAtIndex(i).objectReferenceValue = additionalRenderersList[i];
            serialized.FindProperty("simulatedWaterDepthPercent").floatValue = 72f;
            serialized.FindProperty("simulatedTdsLevel").floatValue = 238f;
            SerializedProperty phProperty = serialized.FindProperty("simulatedPhLevel");
            if (phProperty != null)
                phProperty.floatValue = 7.2f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        private static void BuildGround(Transform parent, Material ground, Material[] strata)
        {
            GameObject geology = new GameObject("Geological_Cutaway");
            geology.transform.SetParent(parent, false);

            // Split surface slab leaves the bore visible while clearly establishing ground level.
            CreatePrimitive("Ground_Surface_Left", PrimitiveType.Cube, geology.transform,
                new Vector3(-3.4f, -0.08f, 0.4f), new Vector3(4.7f, 0.16f, 6.4f), ground);
            CreatePrimitive("Ground_Surface_Right", PrimitiveType.Cube, geology.transform,
                new Vector3(3.4f, -0.08f, 0.4f), new Vector3(4.7f, 0.16f, 6.4f), ground);

            float[] centers = { -0.48f, -1.42f, -2.55f, -4.05f, -5.65f };
            float[] heights = { 0.8f, 1.0f, 1.2f, 1.7f, 1.45f };
            string[] names = { "Topsoil", "Sandstone", "Clay", "Aquifer", "Bedrock" };
            for (int i = 0; i < centers.Length; i++)
            {
                CreatePrimitive(names[i] + "_Back", PrimitiveType.Cube, geology.transform,
                    new Vector3(0f, centers[i], 2.95f), new Vector3(10.4f, heights[i], 0.44f), strata[i]);
                CreatePrimitive(names[i] + "_Left", PrimitiveType.Cube, geology.transform,
                    new Vector3(-5.05f, centers[i], 0.6f), new Vector3(0.32f, heights[i], 4.2f), strata[i]);
            }

            for (int i = 0; i < centers.Length - 1; i++)
            {
                float boundary = centers[i] - heights[i] * 0.5f;
                CreatePrimitive("Strata_Divider_" + i, PrimitiveType.Cube, geology.transform,
                    new Vector3(0f, boundary, 2.70f), new Vector3(10.45f, 0.018f, 0.025f), ground);
            }
        }

        private static void BuildSimulatedNetwork(Transform parent, Material markerMaterial)
        {
            GameObject network = new GameObject("Simulated_Well_Network");
            network.transform.SetParent(parent, false);
            Vector3[] positions =
            {
                new Vector3(-6.4f, 0.1f, 4.8f), new Vector3(-3.9f, 0.1f, 6.2f),
                new Vector3(0.2f, 0.1f, 7.2f), new Vector3(4.2f, 0.1f, 5.8f),
                new Vector3(6.4f, 0.1f, 2.8f), new Vector3(-7.2f, 0.1f, 0.8f),
                new Vector3(7.4f, 0.1f, -1.2f), new Vector3(-5.8f, 0.1f, -4.5f)
            };

            for (int i = 0; i < positions.Length; i++)
            {
                GameObject marker = CreatePrimitive("Simulated_Well_" + (i + 1).ToString("00"),
                    PrimitiveType.Cylinder, network.transform,
                    new Vector3(positions[i].x, 0.04f, positions[i].z),
                    new Vector3(0.12f, 0.035f, 0.12f), markerMaterial);
                marker.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static void ConfigureCameraAndLights(Transform parent)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.transform.position = new Vector3(7.6f, 2.8f, -13.8f);
            camera.transform.LookAt(new Vector3(0f, -2.65f, 0.25f));
            camera.fieldOfView = 36f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Navy;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;

            Light directional = Object.FindFirstObjectByType<Light>();
            if (directional == null || directional.type != LightType.Directional)
            {
                GameObject lightObject = new GameObject("Directional Light");
                directional = lightObject.AddComponent<Light>();
                directional.type = LightType.Directional;
            }
            directional.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
            directional.color = new Color(0.92f, 0.95f, 0.98f);
            directional.intensity = 1.05f;

            CreatePointLight("Neutral_Fill", parent, new Vector3(-4f, 2f, -5f),
                new Color(0.72f, 0.82f, 0.86f), 2.1f, 14f);
            CreatePointLight("Subsurface_Fill", parent, new Vector3(3f, -2.5f, -3f),
                new Color(0.58f, 0.69f, 0.68f), 1.4f, 10f);
        }

        private static void BuildDashboard(WellVisualizer wellVisualizer)
        {
            GameObject canvasObject = new GameObject("DashboardCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;
            canvas.pixelPerfect = true;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform header = CreatePanel("ApplicationBar", canvasObject.transform, Hex("12181E", 0.985f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(0f, 72f));
            CreateText("Title", header, "AQUATWIN", 27f, FontStyles.Bold, MainText,
                TextAlignmentOptions.Left, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(28f, 0f), new Vector2(180f, 38f));
            CreatePanel("BrandDivider", header, Cyan,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(196f, 0f), new Vector2(2f, 28f));
            CreateText("Context", header, "Regional Network   /   ESP32-WELL-01", 16f, FontStyles.Normal, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(222f, 0f), new Vector2(390f, 30f));

            RectTransform status = CreatePanel("ConnectionStatus", header, Hex("1D2925", 1f),
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-24f, 0f), new Vector2(154f, 32f));
            CreateText("Connection", status, "●   LIVE  ·  2s", 14f, FontStyles.Bold, Green,
                TextAlignmentOptions.Right, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                Vector2.zero, new Vector2(130f, 24f));

            RectTransform assetPanel = CreatePanel("AssetContextPanel", canvasObject.transform, Panel,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -96f), new Vector2(340f, 380f));
            CreateText("PanelLabel", assetPanel, "SELECTED ASSET", 13f, FontStyles.Bold, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(22f, -24f), new Vector2(-44f, 22f));
            CreateText("AssetName", assetPanel, "Well ESP32-01", 29f, FontStyles.Bold, MainText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(22f, -62f), new Vector2(-44f, 34f));
            CreateText("AssetType", assetPanel, "Physical demonstration well", 16f, FontStyles.Normal, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(22f, -94f), new Vector2(-44f, 24f));
            CreateDivider(assetPanel, 126f);
            CreateInfoRow(assetPanel, "SOURCE", "ESP32 / Serial", 156f);
            CreateInfoRow(assetPanel, "DEPTH RANGE", "0 – 6.0 m", 204f);
            CreateInfoRow(assetPanel, "AQUIFER", "Demo aquifer layer", 252f);
            CreateInfoRow(assetPanel, "LAST SAMPLE", "2 seconds ago", 300f);

            RectTransform telemetry = CreatePanel("TelemetryPanel", canvasObject.transform, Panel,
                Vector2.one, Vector2.one, Vector2.one,
                new Vector2(-24f, -84f), new Vector2(410f, 800f));
            CreateText("PanelTitle", telemetry, "Live telemetry", 25f, FontStyles.Bold, MainText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -28f), new Vector2(-48f, 30f));
            CreateText("Timestamp", telemetry, "Updated 17:05:01", 14f, FontStyles.Normal, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -57f), new Vector2(-48f, 20f));
            CreateDivider(telemetry, 82f);

            CreateText("WaterLabel", telemetry, "WATER COLUMN", 13f, FontStyles.Bold, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -108f), new Vector2(-48f, 22f));
            TextMeshProUGUI waterValueText = CreateText("WaterValue", telemetry, "72.0%", 46f, FontStyles.Bold, MainText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -151f), new Vector2(170f, 48f));
            TextMeshProUGUI depthFromTopText = CreateText("WaterDepth", telemetry, "DEPTH TO WATER\n1.68 m below top", 14f, FontStyles.Normal, SecondaryText,
                TextAlignmentOptions.Right, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-24f, -142f), new Vector2(176f, 48f));
            RectTransform track = CreatePanel("WaterTrack", telemetry, Divider,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -192f), new Vector2(-48f, 6f));
            RectTransform fill = CreatePanel("ProgressFill", track, Cyan,
                new Vector2(0f, 0f), new Vector2(0.72f, 1f), new Vector2(0f, 0.5f),
                Vector2.zero, Vector2.zero);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            CreateDivider(telemetry, 224f);

            CreateTelemetryRow(telemetry, "TOTAL DISSOLVED SOLIDS", "238", "ppm", "Within target", Green, 252f);
            CreateDivider(telemetry, 360f);
            CreateTelemetryRow(telemetry, "WATER PH", "7.20", "pH", "Within target", Green, 388f);
            CreateDivider(telemetry, 496f);
            CreateTelemetryRow(telemetry, "REGIONAL RESERVE", "72", "%", "Stable this week", Cyan, 524f);
            CreateDivider(telemetry, 632f);
            CreateTelemetryRow(telemetry, "NETWORK AVAILABILITY", "01 / 08", "live / simulated", "All feeds nominal", Green, 660f);

            RectTransform legend = CreatePanel("GeologyLegend", canvasObject.transform, Panel,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(24f, 24f), new Vector2(340f, 190f));
            CreateText("LegendTitle", legend, "SUBSURFACE PROFILE", 13f, FontStyles.Bold, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(20f, -22f), new Vector2(-40f, 22f));
            CreateText("LegendItems", legend,
                "0.0 m   Surface seal\n0.4 m   Topsoil\n1.0 m   Sandstone\n2.0 m   Clay\n3.2 m   Aquifer\n5.0 m   Bedrock",
                14f, FontStyles.Normal, MainText, TextAlignmentOptions.TopLeft,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(20f, -50f), new Vector2(-40f, 126f));

            RegionalDashboardController controller = canvasObject.AddComponent<RegionalDashboardController>();
            SerializedObject dashboardData = new SerializedObject(controller);
            dashboardData.FindProperty("wellVisualizer").objectReferenceValue = wellVisualizer;
            dashboardData.FindProperty("waterPercentText").objectReferenceValue = waterValueText;
            dashboardData.FindProperty("depthFromTopText").objectReferenceValue = depthFromTopText;
            dashboardData.FindProperty("tdsValueText").objectReferenceValue =
                telemetry.Find("TOTAL_DISSOLVED_SOLIDS_Value").GetComponent<TextMeshProUGUI>();
            dashboardData.FindProperty("tdsStatusText").objectReferenceValue =
                telemetry.Find("TOTAL_DISSOLVED_SOLIDS_Status").GetComponent<TextMeshProUGUI>();
            dashboardData.FindProperty("phValueText").objectReferenceValue =
                telemetry.Find("WATER_PH_Value").GetComponent<TextMeshProUGUI>();
            dashboardData.FindProperty("phStatusText").objectReferenceValue =
                telemetry.Find("WATER_PH_Status").GetComponent<TextMeshProUGUI>();
            dashboardData.FindProperty("waterFill").objectReferenceValue = fill;
            dashboardData.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateDivider(Transform parent, float y)
        {
            CreatePanel("Divider", parent, Divider,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -y), new Vector2(-48f, 1f));
        }

        private static void CreateInfoRow(Transform parent, string label, string value, float y)
        {
            CreateText(label + "_Label", parent, label, 12f, FontStyles.Bold, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(22f, -y), new Vector2(95f, 20f));
            CreateText(label + "_Value", parent, value, 15f, FontStyles.Normal, MainText,
                TextAlignmentOptions.Right, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-22f, -y), new Vector2(155f, 22f));
        }

        private static void CreateTelemetryRow(
            Transform parent, string label, string value, string unit, string status, Color statusColor, float y)
        {
            CreateText(label.Replace(" ", "_") + "_Label", parent, label, 13f, FontStyles.Bold, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -y), new Vector2(-48f, 22f));
            CreateText(label.Replace(" ", "_") + "_Value", parent, value, 38f, FontStyles.Bold, MainText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(24f, -y - 42f), new Vector2(150f, 44f));
            CreateText(label.Replace(" ", "_") + "_Unit", parent, unit, 14f, FontStyles.Normal, SecondaryText,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(150f, -y - 42f), new Vector2(150f, 26f));
            CreateText(label.Replace(" ", "_") + "_Status", parent, "●  " + status, 12f, FontStyles.Normal, statusColor,
                TextAlignmentOptions.Left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -y - 78f), new Vector2(-48f, 22f));
        }

        private static RectTransform CreatePanel(
            string name, Transform parent, Color color, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
            obj.transform.SetParent(parent, false);
            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
            Image image = obj.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static void CreateAccent(Transform parent, bool horizontal)
        {
            RectTransform accent = CreatePanel("Accent", parent, Cyan,
                horizontal ? new Vector2(0f, 0f) : new Vector2(0f, 0f),
                horizontal ? new Vector2(1f, 0f) : new Vector2(0f, 1f),
                Vector2.zero, Vector2.zero,
                horizontal ? new Vector2(0f, 3f) : new Vector2(3f, 0f));
            accent.offsetMin = Vector2.zero;
            accent.offsetMax = horizontal ? new Vector2(0f, 3f) : new Vector2(3f, 0f);
        }

        private static TextMeshProUGUI CreateText(
            string name, Transform parent, string value, float size, FontStyles style, Color color,
            TextAlignmentOptions alignment, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(
                Mathf.Approximately(anchorMin.x, anchorMax.x) ? anchorMin.x : 0.5f,
                Mathf.Approximately(anchorMin.y, anchorMax.y) ? anchorMin.y : 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;

            TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
            text.text = value;
            text.font = TMP_Settings.defaultFontAsset;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.enableAutoSizing = false;
            text.extraPadding = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return text;
        }

        private static GameObject CreatePrimitive(
            string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = scale;
            Renderer renderer = obj.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            Collider collider = obj.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
            return obj;
        }

        private static Material CreateLitMaterial(
            string name, Color color, float metallic, float smoothness, bool transparent, int priority)
        {
            string path = MaterialsFolder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = FindCompatibleLitShader();
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);

            bool isUrp = material.shader.name.StartsWith("Universal Render Pipeline");
            if (transparent)
            {
                if (isUrp)
                {
                    if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                    if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                }
                else
                {
                    if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 2f);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.EnableKeyword("_ALPHABLEND_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                }

                if (material.HasProperty("_SrcBlend"))
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend"))
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                material.renderQueue = (int)RenderQueue.Transparent + priority;
            }
            else
            {
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.One);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 1f);
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)RenderQueue.Geometry;
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Shader FindCompatibleLitShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
                return shader;

            shader = Shader.Find("Standard");
            if (shader != null)
                return shader;

            Material defaultMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            if (defaultMaterial != null && defaultMaterial.shader != null)
                return defaultMaterial.shader;

            shader = Shader.Find("Unlit/Color");
            if (shader != null)
                return shader;

            throw new System.InvalidOperationException("Unity could not locate a compatible material shader.");
        }

        private static Material CreateEmissionMaterial(string name, Color color, float intensity)
        {
            Material material = CreateLitMaterial(name, color, 0.1f, 0.72f, false, 0);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * intensity);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void CreatePointLight(
            string name, Transform parent, Vector3 position, Color color, float intensity, float range)
        {
            GameObject obj = new GameObject(name, typeof(Light));
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            Light light = obj.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        private static Color Hex(string rgb, float alpha = 1f)
        {
            ColorUtility.TryParseHtmlString("#" + rgb, out Color color);
            color.a = alpha;
            return color;
        }
    }
}
