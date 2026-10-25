using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AquaTwin
{
    /// <summary>Procedural schematic cutaway. Mesh blending is illustrative, not a hydrological model.</summary>
    public sealed class FleetScene : MonoBehaviour
    {
        private sealed class Node
        {
            public Transform Root, Water, Halo, Head;
            public Renderer WaterRenderer, HeadRenderer;
            public Vector3 Position;
        }
        private readonly Dictionary<string, Node> nodes = new();
        private readonly List<Material> materials = new();
        private Transform geology, surfaceRoot;
        private Mesh surface;
        private Renderer surfaceRenderer;
        private Camera view;
        private RenderTexture texture;
        private Material steel, water, glow, soil, grid;
        private MaterialPropertyBlock block;
        private Vector2 span = new(16, 9);
        private float yaw = -17, pitch = 18, zoom = 1;
        private int count = -1;
        private string layoutKey;
        public bool ShowGeology = true, ShowSurface = true;
        public Camera Camera => view;
        public RenderTexture Texture => texture;
        public bool Geographic { get; private set; }
        public bool EnvelopeIsLastKnown { get; private set; }
        public Vector3 HeadPosition(string id) => nodes.TryGetValue(id, out Node node) ? node.Head.position + Vector3.up * 0.5f : Vector3.zero;
        public string Pick(Vector2 normalized)
        {
            if (Physics.Raycast(view.ViewportPointToRay(normalized), out RaycastHit hit, 1000) && nodes.ContainsKey(hit.transform.name))
                return hit.transform.name;
            return null;
        }

        public void Initialize()
        {
            // Unity native objects must be created on the main thread after AddComponent,
            // never in a MonoBehaviour constructor or instance field initializer.
            block = new MaterialPropertyBlock();
            foreach (Camera old in FindObjectsByType<Camera>(FindObjectsSortMode.None)) old.enabled = false;
            // UI Toolkit composites the dashboard over Display 1, while the 3D camera
            // renders only to a texture. Keep a screen camera to clear the display
            // and prevent Unity's "No cameras rendering" Game view overlay.
            var displayObject = new GameObject("Dashboard display camera");
            displayObject.transform.SetParent(transform, false);
            var displayCamera = displayObject.AddComponent<Camera>();
            displayCamera.targetDisplay = 0;
            displayCamera.targetTexture = null;
            displayCamera.rect = new Rect(0, 0, 1, 1);
            displayCamera.clearFlags = CameraClearFlags.SolidColor;
            displayCamera.backgroundColor = new Color(.035f, .075f, .102f);
            displayCamera.cullingMask = 0;
            displayCamera.depth = -100;
            displayCamera.allowHDR = false;
            displayCamera.allowMSAA = false;
            steel = Mat("Well casing", new Color(.33f, .48f, .51f), true);
            water = Mat("Water column", new Color(.15f, .84f, .72f), true);
            glow = Mat("Sensor beacon", new Color(.31f, .96f, .80f), false);
            soil = Mat("Geology", new Color(.28f, .29f, .27f), true);
            grid = Mat("Reference grid", new Color(.11f, .21f, .24f), false);
            var cameraObject = new GameObject("Fleet camera");
            cameraObject.transform.SetParent(transform, false);
            view = cameraObject.AddComponent<Camera>();
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = new Color(.025f, .064f, .082f);
            view.orthographic = true;
            view.nearClipPlane = .1f;
            view.farClipPlane = 1000;
            view.allowHDR = false;
            var lightObject = new GameObject("Fleet key light");
            lightObject.transform.SetParent(transform, false);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.35f;
            light.color = new Color(.80f, .93f, 1);
            light.transform.rotation = Quaternion.Euler(35, -30, 0);
            RenderSettings.ambientLight = new Color(.32f, .39f, .42f);
            surfaceRoot = new GameObject("Estimated water envelope").transform;
            surfaceRoot.SetParent(transform, false);
            surface = new Mesh { name = "Schematic water envelope" };
            surfaceRoot.gameObject.AddComponent<MeshFilter>().sharedMesh = surface;
            surfaceRenderer = surfaceRoot.gameObject.AddComponent<MeshRenderer>();
            surfaceRenderer.sharedMaterial = Mat("Aquifer envelope", new Color(.12f, .70f, .66f, .28f), false, true);
            Resize(1000, 650);
        }

        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 64, 2048);
            height = Mathf.Clamp(height, 64, 2048);
            if (texture != null && texture.width == width && texture.height == height) return;
            if (texture != null) { texture.Release(); Destroy(texture); }
            texture = new RenderTexture(width, height, 24) { name = "AquaTwin viewport", antiAliasing = 4 };
            texture.Create();
            view.targetTexture = texture;
            FitCamera();
        }

        public void Orbit(Vector2 delta) { yaw += delta.x * .22f; pitch = Mathf.Clamp(pitch + delta.y * .18f, 6, 65); FitCamera(); }
        public void Zoom(float delta) { zoom = Mathf.Clamp(zoom * Mathf.Exp(delta * .06f), .45f, 2.5f); FitCamera(); }
        public void ResetView() { yaw = -17; pitch = 18; zoom = 1; FitCamera(); }
        private void FitCamera()
        {
            if (view == null) return;
            Vector3 target = new(0, -2.6f, 0);
            view.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            view.transform.position = target - view.transform.forward * 100;
            float aspect = texture == null ? 1.5f : (float)texture.width / texture.height;
            float horizontal = (span.x + span.y * .55f + 3) / Mathf.Max(.3f, aspect);
            float vertical = 9 + span.y * .5f;
            view.orthographicSize = Mathf.Max(horizontal, vertical) * .55f * zoom;
        }

        public void Refresh(WellFleet fleet, string selected, float now, float timeout, float reviewTds = 600)
        {
            // Re-layout when identities or coordinates change, not at sensor update frequency.
            string key = string.Join("|", System.Linq.Enumerable.Select(fleet.Wells,
                w => w.Id + ":" + w.HasLocation + ":" + w.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + w.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            if (count != fleet.Wells.Count || layoutKey != key)
            {
                Layout(fleet);
                count = fleet.Wells.Count;
                layoutKey = key;
            }
            geology.gameObject.SetActive(ShowGeology);
            surfaceRoot.gameObject.SetActive(ShowSurface && fleet.Wells.Count > 0);
            foreach (WellFleet.Well well in fleet.Wells)
            {
                Node node = nodes[well.Id];
                float height = Mathf.Max(.025f, well.Fill * 6);
                node.Water.localPosition = new Vector3(0, -6 + height / 2, 0);
                node.Water.localScale = new Vector3(.32f, height / 2, .32f);
                Color color = QualityColor(well.Tds, reviewTds);
                if (!well.IsFresh(now, timeout)) color = Color.Lerp(color, Color.gray, .8f);
                Tint(node.WaterRenderer, color);
                Tint(node.HeadRenderer, color);
                node.Halo.gameObject.SetActive(well.Id == selected);
            }
            UpdateSurface(fleet, now, timeout);
        }

        private void Layout(WellFleet fleet)
        {
            int count = fleet.Wells.Count;
            foreach (Node node in nodes.Values) Destroy(node.Root.gameObject);
            nodes.Clear();
            int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1, count) * 1.6f)));
            int rows = Mathf.Max(1, Mathf.CeilToInt((float)count / columns));
            span = new Vector2(Mathf.Max(14, columns * 3.2f), Mathf.Max(7, rows * 3.1f));
            Geographic = count > 1;
            double minLat = 90, maxLat = -90, minLng = 180, maxLng = -180;
            foreach (var w in fleet.Wells)
            {
                Geographic &= w.HasLocation;
                minLat = Math.Min(minLat, w.Latitude); maxLat = Math.Max(maxLat, w.Latitude);
                minLng = Math.Min(minLng, w.Longitude); maxLng = Math.Max(maxLng, w.Longitude);
            }
            Geographic &= maxLat - minLat > .000001 || maxLng - minLng > .000001;
            // Duplicate locations use the schematic grid so no bore is hidden behind another.
            var locations = new HashSet<string>();
            foreach (var w in fleet.Wells)
                if (!locations.Add(w.Latitude.ToString("R") + "/" + w.Longitude.ToString("R"))) Geographic = false;
            for (int i = 0; i < count; i++)
            {
                var w = fleet.Wells[i];
                float x = Geographic ? (float)((w.Longitude - minLng) / Math.Max(.000001, maxLng - minLng) - .5) * (span.x - 4) : (i % columns - (columns - 1) / 2f) * 3.2f;
                float z = Geographic ? (float)((w.Latitude - minLat) / Math.Max(.000001, maxLat - minLat) - .5) * (span.y - 3) : (i / columns - (rows - 1) / 2f) * 3.1f;
                var root = new GameObject(w.Id).transform;
                root.SetParent(transform, false);
                root.localPosition = new Vector3(x, 0, z);
                var hitArea = root.gameObject.AddComponent<BoxCollider>();
                hitArea.center = new Vector3(0, -2.7f, 0);
                hitArea.size = new Vector3(.85f, 6.5f, .85f);
                var node = new Node { Root = root, Position = root.localPosition };
                for (int rail = 0; rail < 2; rail++)
                    Primitive("Casing rail", PrimitiveType.Cylinder, root, new Vector3(rail == 0 ? -.27f : .27f, -3, 0), new Vector3(.055f, 3, .055f), steel);
                for (int ring = 0; ring <= 6; ring++)
                    Primitive("Depth collar", PrimitiveType.Cylinder, root, new Vector3(0, -ring, 0), new Vector3(.62f, .025f, .62f), steel);
                node.Water = Primitive("Water", PrimitiveType.Cylinder, root, Vector3.zero, Vector3.one, water).transform;
                node.WaterRenderer = node.Water.GetComponent<Renderer>();
                node.Head = Primitive("Sensor", PrimitiveType.Cylinder, root, new Vector3(0, .22f, 0), new Vector3(.72f, .18f, .72f), steel).transform;
                var beacon = Primitive("Beacon", PrimitiveType.Sphere, root, new Vector3(0, .52f, 0), Vector3.one * .20f, glow);
                node.HeadRenderer = beacon.GetComponent<Renderer>();
                node.Halo = Primitive("Selected well", PrimitiveType.Cylinder, root, new Vector3(0, -.03f, 0), new Vector3(1.2f, .018f, 1.2f), glow).transform;
                nodes.Add(w.Id, node);
            }
            BuildGeology();
            FitCamera();
        }

        private void BuildGeology()
        {
            if (geology != null) Destroy(geology.gameObject);
            geology = new GameObject("Geological cutaway").transform;
            geology.SetParent(transform, false);
            // One shared material plus property blocks avoids material growth on discovery.
            Color[] colors = { new(.27f,.29f,.26f), new(.38f,.34f,.26f), new(.28f,.29f,.27f), new(.20f,.31f,.31f), new(.20f,.24f,.26f) };
            string[] names = { "Topsoil", "Sand", "Clay", "Aquifer", "Bedrock" };
            float[] heights = { .5f, 1.2f, 1.3f, 2, 1.3f };
            float y = 0;
            for (int i = 0; i < heights.Length; i++)
            {
                y -= heights[i] / 2;
                var back = Primitive(names[i], PrimitiveType.Cube, geology, new Vector3(0, y, span.y / 2 + .6f), new Vector3(span.x + 2, heights[i] - .025f, .65f), soil);
                Tint(back.GetComponent<Renderer>(), colors[i]);
                var side = Primitive(names[i] + " side", PrimitiveType.Cube, geology, new Vector3(-span.x / 2 - .85f, y, 0), new Vector3(.3f, heights[i] - .025f, span.y + 1.9f), soil);
                Tint(side.GetComponent<Renderer>(), colors[i]);
                y -= heights[i] / 2;
            }
            Primitive("Base", PrimitiveType.Cube, geology, new Vector3(0, -6.5f, 0), new Vector3(span.x + 2, .12f, span.y + 2), soil);
            for (int i = 0; i <= 12; i++)
            {
                Primitive("Grid X", PrimitiveType.Cube, geology, new Vector3((i / 12f - .5f) * (span.x + 2), -6.42f, 0), new Vector3(.018f, .015f, span.y + 2), grid);
                Primitive("Grid Z", PrimitiveType.Cube, geology, new Vector3(0, -6.42f, (i / 12f - .5f) * (span.y + 2)), new Vector3(span.x + 2, .015f, .018f), grid);
            }
        }

        private void UpdateSurface(WellFleet fleet, float now, float timeout)
        {
            const int resolution = 24;
            var vertices = new Vector3[(resolution + 1) * (resolution + 1)];
            var colors = new Color[vertices.Length];
            var triangles = new int[resolution * resolution * 6];
            int freshCount = 0;
            foreach (var w in fleet.Wells) if (w.IsFresh(now, timeout)) freshCount++;
            EnvelopeIsLastKnown = freshCount == 0 && fleet.Wells.Count > 0;
            surfaceRenderer.enabled = fleet.Wells.Count > 0;
            if (fleet.Wells.Count == 0) return;
            // Preserve a clearly muted last-known envelope during source pauses.
            // When current readings exist, only those contribute to the live surface.
            Tint(surfaceRenderer, EnvelopeIsLastKnown
                ? new Color(.40f, .49f, .51f, .18f) : new Color(.12f, .70f, .66f, .28f));
            for (int z = 0; z <= resolution; z++)
            for (int x = 0; x <= resolution; x++)
            {
                Vector3 position = new((x / (float)resolution - .5f) * span.x, 0, (z / (float)resolution - .5f) * span.y);
                float sum = 0, height = 0;
                foreach (var w in fleet.Wells)
                {
                    if (!EnvelopeIsLastKnown && !w.IsFresh(now, timeout)) continue;
                    float weight = 1 / Mathf.Max(.01f, (position - nodes[w.Id].Position).sqrMagnitude);
                    height += (-6 + w.Fill * 6) * weight;
                    sum += weight;
                }
                position.y = height / Mathf.Max(.001f, sum);
                int index = z * (resolution + 1) + x;
                vertices[index] = position;
                colors[index] = Color.white;
                if (x == resolution || z == resolution) continue;
                int t = (z * resolution + x) * 6;
                triangles[t] = index; triangles[t + 1] = index + resolution + 1; triangles[t + 2] = index + 1;
                triangles[t + 3] = index + 1; triangles[t + 4] = index + resolution + 1; triangles[t + 5] = index + resolution + 2;
            }
            surface.Clear(); surface.vertices = vertices; surface.triangles = triangles; surface.colors = colors;
            surface.RecalculateNormals(); surface.RecalculateBounds();
        }

        public static Color QualityColor(float tds, float reviewThreshold = 600)
        {
            float threshold = Mathf.Max(.01f, reviewThreshold);
            return tds <= threshold
                ? Color.Lerp(new Color(.16f,.82f,.68f), new Color(.83f,.77f,.28f), tds / threshold)
                : Color.Lerp(new Color(.83f,.77f,.28f), new Color(.94f,.34f,.27f), Mathf.Clamp01((tds - threshold) / (threshold * 1.5f)));
        }
        private void Tint(Renderer renderer, Color color) { renderer.GetPropertyBlock(block); block.SetColor("_Color", color); renderer.SetPropertyBlock(block); }
        private Material Mat(string label, Color color, bool lit, bool transparent = false)
        {
            var material = new Material(Resources.Load<Shader>("FleetSurface")) { name = label, color = color };
            material.SetFloat("_Lit", lit ? 1 : 0);
            material.SetFloat("_ZWrite", transparent ? 0 : 1);
            if (transparent) material.renderQueue = 3000;
            materials.Add(material); return material;
        }
        private static GameObject Primitive(string label, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var item = GameObject.CreatePrimitive(type); item.name = label; item.transform.SetParent(parent, false);
            item.transform.localPosition = position; item.transform.localScale = scale;
            Destroy(item.GetComponent<Collider>());
            var renderer = item.GetComponent<Renderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return item;
        }
        private void OnDestroy()
        {
            if (texture != null) { texture.Release(); Destroy(texture); }
            if (surface != null) Destroy(surface);
            foreach (var material in materials) Destroy(material);
        }
    }
}
