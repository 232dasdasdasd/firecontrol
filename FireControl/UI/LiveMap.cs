#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using com.crenetic.Notruf2019.Game;
using com.crenetic.Notruf2019.Situation;
using com.crenetic.N2019Plugins.GameUnits;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Notruf112ModMenu
{
    public partial class ModMenu : BaseUnityPlugin
    {
        // Hallentor-Fix: ausschließlich für den lokalen Avatar während einer Door/Gateway-Interaktion.
        // Fix ist im laufenden Spiel dauerhaft aktiv. Kein Eingriff in Toranimation oder andere KI.

        private bool AcquireLiveMap()
        {
            if (liveMapCamera != null && liveMapCamera.targetTexture != null) return true;
            try
            {
                Type? type = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.UI.MapView");
                if (type == null) { mapInfo = "MapView-Klasse nicht gefunden"; return false; }
                // Auch deaktivierte F5-Karten finden (Awake setzt SetVisible(false)).
                UnityEngine.Object[] maps = Resources.FindObjectsOfTypeAll(type);
                foreach (UnityEngine.Object candidate in maps)
                {
                    Component? component = candidate as Component;
                    if (component == null || !component.gameObject.scene.IsValid()) continue;
                    Camera? cam = ReadField(candidate, "mapCamera") as Camera;
                    if (cam == null || cam.targetTexture == null) continue;
                    liveMapCamera = cam;
                    mapOriginalCameraPosition = cam.transform.position;
                    mapOriginalCameraRotation = cam.transform.rotation;
                    mapOriginalOrthographic = cam.orthographic;
                    mapOriginalFieldOfView = cam.fieldOfView;
                    mapOriginalFarClip = cam.farClipPlane;
                    mapOriginalNearClip = cam.nearClipPlane;
                    mapOriginalLayerCullDistances = cam.layerCullDistances;
                    mapOriginalLayerCullSpherical = cam.layerCullSpherical;
                    mapOriginalCullingMask = cam.cullingMask;
                    mapOriginalBackgroundColor = cam.backgroundColor;
                    mapOriginalClearFlags = cam.clearFlags;
                    mapOriginalAllowHDR = cam.allowHDR;
                    mapCameraStateCaptured = true;
                    mapHomePosition = cam.transform.position;
                    mapHomeZoom = cam.orthographicSize;
                    mapReplacementShader = ReadField(candidate, "mapShader") as Shader;
                    liveMapCameraObject = cam.gameObject;
                    liveMapCameraWasActive = cam.gameObject.activeSelf;
                    liveMapCameraWasEnabled = cam.enabled;
                    mapZoom = Mathf.Clamp(cam.orthographicSize, 8f, 1800f);
                    liveMapOriginalTexture = cam.targetTexture;
                    // Kleine separate RenderTexture statt der bildschirmgrossen Originaltextur.
                    // Die Original-F5-Karte bleibt unberuehrt und wird beim Verlassen wiederhergestellt.
                    CreateMapTexture();
                    cam.targetTexture = liveMapReducedTexture;
                    ConfigureMapVisuals();
                    cam.gameObject.SetActive(true);
                    cam.enabled = false; // Nicht jeden Frame die gesamte Welt rendern!
                    mapNextRender = 0f;
                    mapBackgroundDirty = true;
                    mapLastRenderedZoom = -1f;
                    mapInfo = "Originalkarte verbunden (Performance-Modus)";
                    return true;
                }
                mapInfo = "F5-Karte noch nicht initialisiert (Spielwelt laden)";
            }
            catch (Exception ex)
            {
                mapInfo = "Kartenfehler: " + ex.GetBaseException().Message;
                ;
            }
            return false;
        }

        private void ReleaseLiveMap()
        {
            try
            {
                if (liveMapCamera != null)
                {
                    if (mapCameraStateCaptured)
                    {
                        liveMapCamera.transform.position = mapOriginalCameraPosition;
                        liveMapCamera.transform.rotation = mapOriginalCameraRotation;
                        liveMapCamera.orthographic = mapOriginalOrthographic;
                        liveMapCamera.fieldOfView = mapOriginalFieldOfView;
                        liveMapCamera.farClipPlane = mapOriginalFarClip;
                        liveMapCamera.nearClipPlane = mapOriginalNearClip;
                        if (mapOriginalLayerCullDistances != null) liveMapCamera.layerCullDistances = mapOriginalLayerCullDistances;
                        liveMapCamera.layerCullSpherical = mapOriginalLayerCullSpherical;
                        liveMapCamera.cullingMask = mapOriginalCullingMask;
                        liveMapCamera.backgroundColor = mapOriginalBackgroundColor;
                        liveMapCamera.clearFlags = mapOriginalClearFlags;
                        liveMapCamera.allowHDR = mapOriginalAllowHDR;
                        if (mapReplacementShader != null) liveMapCamera.SetReplacementShader(mapReplacementShader, mapReplacementTag);
                    }
                    liveMapCamera.enabled = liveMapCameraWasEnabled;
                    liveMapCamera.targetTexture = liveMapOriginalTexture;
                }
                if (liveMapCameraObject != null)
                    liveMapCameraObject.SetActive(liveMapCameraWasActive);
            }
            catch (Exception) { ; }
            finally
            {
                mapCameraStateCaptured = false;
                liveMapCamera = null;
                liveMapCameraObject = null;
                liveMapOriginalTexture = null;
                if (liveMapReducedTexture != null)
                {
                    liveMapReducedTexture.Release();
                    Destroy(liveMapReducedTexture);
                    liveMapReducedTexture = null;
                }
            }
        }

        private void UpdateLiveMap()
        {
            if (!AcquireLiveMap()) return;
            if (liveMapCamera == null) return;
            if (Time.unscaledTime >= mapNextRefresh)
            {
                mapNextRefresh = Time.unscaledTime + 5f;
                // Fahrzeuge bei Bedarf erneut laden; vorhandene Referenzen bleiben erhalten.
                if (vehicles.Count == 0) RefreshVehicles();
            }
            if (mapFollow && mapFocusVehicle >= 0 && mapFocusVehicle < vehicles.Count)
            {
                GameObject? vehicle = vehicles[mapFocusVehicle].VehicleObject;
                if (vehicle != null)
                {
                    Vector3 position = liveMapCamera.transform.position;
                    Vector3 world = vehicle.transform.position;
                    position.x = world.x;
                    position.z = world.z;
                    liveMapCamera.transform.position = position;
                }
            }
            if (liveMapCamera.orthographic) liveMapCamera.orthographicSize = mapZoom;
            // Bei grossem Zoom die Kartenkamera nicht unnötig hoch platzieren:
            // das vermeidet entfernte LOD-Stufen und fehlende Objekte.
            if (mapVisualMode == 1)
            {
                Vector3 p = liveMapCamera.transform.position;
                float height = Mathf.Clamp(450f + mapZoom * 0.18f, 450f, 850f);
                if (Mathf.Abs(p.y - height) > 0.01f)
                    liveMapCamera.transform.position = new Vector3(p.x, height, p.z);
            }
            // Statische Karte: nur neu zeichnen, wenn sich der Kartenausschnitt
            // tatsaechlich geaendert hat. Fahrzeugmarker sind unabhaengig davon live.
            Vector3 currentPosition = liveMapCamera.transform.position;
            bool moved = float.IsNaN(mapLastRenderedPosition.x) ||
                (currentPosition - mapLastRenderedPosition).sqrMagnitude > 0.01f;
            bool zoomChanged = Mathf.Abs(mapZoom - mapLastRenderedZoom) > 0.01f;
            if ((mapBackgroundDirty || moved || zoomChanged) && Time.unscaledTime >= mapNextRender)
            {
                mapNextRender = Time.unscaledTime + (mapRenderSpeed == 0 ? 3f : mapRenderSpeed == 2 ? 0.45f : 1.25f);
                try
                {
                    // LOD nur fuer diesen seltenen Karten-Render verbessern,
                    // anschliessend globale Qualitaet sofort wiederherstellen.
                    float previousLodBias = QualitySettings.lodBias;
                    try
                    {
                        if (mapVisualMode == 1) QualitySettings.lodBias = Mathf.Max(previousLodBias, 2f);
                        liveMapCamera.Render();
                    }
                    finally { QualitySettings.lodBias = previousLodBias; }
                    mapLastRenderedPosition = currentPosition;
                    mapLastRenderedZoom = mapZoom;
                    mapBackgroundDirty = false;
                }
                catch (Exception ex)
                {
                    mapInfo = "Kartenrenderfehler: " + ex.GetBaseException().Message;
                    mapNextRender = Time.unscaledTime + 3f;
                }
            }
        }

        // Symbolsuche einmalig vorbereiten, nicht pro Fahrzeug und GUI-Event reflektieren.
        private void InitMapSignAtlas()
        {
            if (mapSignAtlas != null && mapGetSprite != null) return;
            Type? type = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.UI.MapView");
            if (type == null) return;
            foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(type))
            {
                Component? component = candidate as Component;
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                object? atlas = ReadField(candidate, "tacticalSigns");
                if (atlas == null) continue;
                MethodInfo? method = atlas.GetType().GetMethod("GetSprite", new Type[] { typeof(string) });
                if (method == null) continue;
                mapSignAtlas = atlas;
                mapGetSprite = method;
                return;
            }
        }

        private Sprite? GetMapVehicleSign(VehicleEntry entry)
        {
            string key = entry.SquadName + "|" + entry.VehicleType;
            if (mapVehicleSigns.TryGetValue(key, out Sprite? cached)) return cached;
            try
            {
                InitMapSignAtlas();
                if (mapSignAtlas == null || mapGetSprite == null) return null;
                string squad = entry.SquadName.Trim();
                string vehicle = entry.VehicleName.Trim();
                // Individuelle Kennungen zuerst, danach die gemeinsame Fahrzeugklasse.
                // So erhalten auch DLK 2, RTW 3, POL 2 usw. die passende Raute.
                List<string> names = new List<string>();
                AddSignCandidate(names, squad);
                AddSignCandidate(names, vehicle);
                AddSignCandidate(names, entry.VehicleType);
                string combined = (squad + " " + vehicle).ToUpperInvariant();
                if (combined.Contains("DLK") || combined.Contains("DREHLEITER")) AddSignCandidate(names, "DLK");
                if (combined.Contains("HLF")) AddSignCandidate(names, "HLF");
                if (combined.Contains("TLF")) { AddSignCandidate(names, "TLF3000"); AddSignCandidate(names, "TLF"); }
                if (combined.Contains("RTW")) AddSignCandidate(names, "RTW");
                if (combined.Contains("POL") || combined.Contains("STREIFE"))
                { AddSignCandidate(names, "POL"); AddSignCandidate(names, "POLIZEI"); }
                if (combined.Contains("NEF")) { AddSignCandidate(names, "NEF_OLD"); AddSignCandidate(names, "NEF"); }
                if (combined.Contains("WLF") || combined.Contains("JERWE")) AddSignCandidate(names, "WLF");
                for (int i = 0; i < names.Count; i++)
                {
                    Sprite? result = mapGetSprite.Invoke(mapSignAtlas, new object[] { "TZ_" + names[i] }) as Sprite;
                    if (result != null)
                    {
                        mapVehicleSigns[key] = result;
                        return result;
                    }
                }
            }
            catch (Exception) { ; }
            if (mapSignAtlas != null) mapVehicleSigns[key] = null;
            return null;
        }

        private static void AddSignCandidate(List<string> names, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string upper = value.Trim().ToUpperInvariant();
            if (!names.Contains(upper)) names.Add(upper);
            string noSpaces = upper.Replace(" ", "").Replace("-", "");
            if (!names.Contains(noSpaces)) names.Add(noSpaces);
            // Die Ziffern der individuellen Fahrzeugkennung sind oft nicht Teil des Atlasnamens.
            string stem = noSpaces.TrimEnd('0','1','2','3','4','5','6','7','8','9','_');
            if (stem.Length > 1 && !names.Contains(stem)) names.Add(stem);
        }

        private void DrawMapSign(Rect rect, Sprite? sprite)
        {
            if (sprite == null || sprite.texture == null) return;
            try
            {
                Rect r = sprite.textureRect;
                Texture2D texture = sprite.texture;
                Rect uv = new Rect(r.x / texture.width, r.y / texture.height,
                    r.width / texture.width, r.height / texture.height);
                GUI.DrawTextureWithTexCoords(rect, texture, uv, true);
            }
            catch (Exception) { /* Manche Atlas-Sprites sind eng gepackt. */ }
        }

        private static string MapVehicleLabel(VehicleEntry entry)
        {
            // Der Squad-Name ist die spielinterne Kennung (z.B. JERWE), nicht
            // der grobe Fahrzeugtyp WLF. Keine Kennungen künstlich umbenennen.
            return string.IsNullOrWhiteSpace(entry.SquadName) ? entry.VehicleName : entry.SquadName;
        }

        private void HandleMapMouse(Rect mapRect, Camera cam)
        {
            Event e = Event.current;
            if (e.type == EventType.MouseUp && e.button == 0) mapDragging = false;
            if (!mapRect.Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel)
            {
                // Scrollen zur Mausposition: der Weltpunkt unter dem Cursor bleibt stehen.
                float u = Mathf.Clamp01((e.mousePosition.x - mapRect.x) / mapRect.width);
                float v = Mathf.Clamp01(1f - (e.mousePosition.y - mapRect.y) / mapRect.height);
                float oldSize = mapZoom;
                float newSize = Mathf.Clamp(oldSize * Mathf.Pow(1.16f, e.delta.y), 8f, 1800f);
                Vector3 cameraPosition = cam.transform.position;
                float aspect = mapRect.width / mapRect.height;
                cameraPosition.x += (u - 0.5f) * 2f * (oldSize - newSize) * aspect;
                cameraPosition.z += (v - 0.5f) * 2f * (oldSize - newSize);
                cam.transform.position = cameraPosition;
                mapZoom = newSize;
                mapFollow = false;
                e.Use();
            }
            else if (e.type == EventType.MouseDown && (e.button == 0 || e.button == 2))
            {
                mapDragging = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && mapDragging)
            {
                float aspect = mapRect.width / mapRect.height;
                Vector3 p = cam.transform.position;
                p.x -= e.delta.x / mapRect.width * 2f * mapZoom * aspect;
                p.z += e.delta.y / mapRect.height * 2f * mapZoom;
                cam.transform.position = p;
                mapFollow = false;
                e.Use();
            }
        }

        // The game's MapView applies a replacement shader (MapType). That shader is why
        // non-road geometry is black. Real-world mode removes it temporarily, revealing
        // actual scene materials, terrain and buildings when their render layers exist.
        private void CreateMapTexture()
        {
            int width = mapRenderQuality == 0 ? 480 : mapRenderQuality == 2 ? 960 : 640;
            int height = width * 3 / 4;
            if (liveMapReducedTexture != null)
            {
                if (liveMapCamera != null) liveMapCamera.targetTexture = null;
                liveMapReducedTexture.Release();
                Destroy(liveMapReducedTexture);
            }
            liveMapReducedTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32);
            liveMapReducedTexture.useMipMap = false;
            liveMapReducedTexture.autoGenerateMips = false;
            liveMapReducedTexture.Create();
            if (liveMapCamera != null) liveMapCamera.targetTexture = liveMapReducedTexture;
            mapBackgroundDirty = true;
        }

        private void ConfigureMapVisuals()
        {
            if (liveMapCamera == null) return;
            Camera cam = liveMapCamera;
            if (mapVisualMode == 0)
            {
                if (mapReplacementShader != null) cam.SetReplacementShader(mapReplacementShader, mapReplacementTag);
                cam.orthographic = true;
                cam.transform.rotation = mapOriginalCameraRotation;
                cam.cullingMask = mapOriginalCullingMask;
                cam.backgroundColor = mapOriginalBackgroundColor;
                cam.clearFlags = mapOriginalClearFlags;
                cam.farClipPlane = mapOriginalFarClip;
                cam.nearClipPlane = mapOriginalNearClip;
                if (mapOriginalLayerCullDistances != null) cam.layerCullDistances = mapOriginalLayerCullDistances;
                cam.layerCullSpherical = mapOriginalLayerCullSpherical;
                cam.transform.position = new Vector3(cam.transform.position.x, mapOriginalCameraPosition.y, cam.transform.position.z);
            }
            else
            {
                cam.ResetReplacementShader();
                cam.cullingMask = ~0; // scene layers, not just MapType-tagged geometry
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.22f, 0.30f, 0.28f, 1f);
                cam.farClipPlane = Mathf.Max(mapOriginalFarClip, 6000f);
                cam.nearClipPlane = 0.3f;
                // Distanz-Culling einzelner Layer fuer die Weltkarte aufheben.
                // Die Kamera rendert weiterhin nur nach Bedarf.
                cam.layerCullDistances = new float[32];
                cam.layerCullSpherical = false;
                cam.allowHDR = false;
                cam.orthographic = true;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            mapBackgroundDirty = true;
            mapNextRender = 0f;
            mapLastRenderedZoom = -1f;
        }

        private void SetMapMode(int mode)
        {
            if (mapVisualMode == mode) return;
            mapVisualMode = mode;
            ConfigureMapVisuals();
            mapInfo = mode == 0 ? "Originalkarte" : "Weltkarte: echte Szenenmaterialien";
        }

        private bool MapEntryActive(VehicleEntry entry)
        {
            string state = entry.Status ?? "";
            return state.IndexOf("DutyHome", StringComparison.OrdinalIgnoreCase) < 0 &&
                state.IndexOf("Home", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private bool MapEntryVisible(VehicleEntry entry)
        {
            if (!mapShowAllVehicles) return false;
            bool active = MapEntryActive(entry);
            return (active && mapShowActive) || (!active && mapShowAvailable);
        }

        private void FocusMapVehicle(int index)
        {
            mapFocusVehicle = index;
            mapFollow = true;
            mapBackgroundDirty = true;
            if (index >= 0 && index < vehicles.Count)
                statusMessage = "Kartenfokus: " + MapVehicleLabel(vehicles[index]);
        }

        private void DrawLiveMapTab()
        {
            GUILayout.Label("LIVE-KARTE 2.1  |  WELTKARTE & FAHRZEUGTRACKING", headingStyle);
            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(smallButtonStyle);
                mapMarkerStyle.fontSize = 12;
                mapMarkerStyle.alignment = TextAnchor.MiddleCenter;
            }
            GUILayout.Label("Weltkarte zeigt die Spielmaterialien statt des vereinfachten Straßen-Shaders. " +
                "Sichtweite und Detailstufen sind fuer grosse Kartenausschnitte optimiert.", infoStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("ORIGINAL", mapVisualMode == 0 ? activeButtonStyle : buttonStyle, GUILayout.Width(135))) SetMapMode(0);
            if (GUILayout.Button("WELTKARTE", mapVisualMode == 1 ? activeButtonStyle : buttonStyle, GUILayout.Width(155))) SetMapMode(1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("FAHRZEUGE AKTUALISIEREN", buttonStyle, GUILayout.Width(245)))
            {
                RefreshVehicles(); mapVehicleSigns.Clear(); mapSignAtlas = null;
                mapGetSprite = null; mapFocusVehicle = -1;
            }
            if (GUILayout.Button(mapFollow ? "FOLGEN: AN" : "FOLGEN: AUS", buttonStyle, GUILayout.Width(145))) mapFollow = !mapFollow;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("QUALITÄT", infoStyle, GUILayout.Width(75));
            if (GUILayout.Button("ECO", mapRenderQuality == 0 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(70))) { mapRenderQuality = 0; if (liveMapCamera != null) CreateMapTexture(); }
            if (GUILayout.Button("NORMAL", mapRenderQuality == 1 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(90))) { mapRenderQuality = 1; if (liveMapCamera != null) CreateMapTexture(); }
            if (GUILayout.Button("SCHARF", mapRenderQuality == 2 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(85))) { mapRenderQuality = 2; if (liveMapCamera != null) CreateMapTexture(); }
            GUILayout.Space(12);
            GUILayout.Label("UPDATE", infoStyle, GUILayout.Width(65));
            if (GUILayout.Button("SPARSAM", mapRenderSpeed == 0 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(90))) mapRenderSpeed = 0;
            if (GUILayout.Button("NORMAL", mapRenderSpeed == 1 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(85))) mapRenderSpeed = 1;
            if (GUILayout.Button("SCHNELL", mapRenderSpeed == 2 ? activeButtonStyle : smallButtonStyle, GUILayout.Width(90))) mapRenderSpeed = 2;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            mapShowAllVehicles = GUILayout.Toggle(mapShowAllVehicles, "MARKER", GUILayout.Width(100));
            mapShowActive = GUILayout.Toggle(mapShowActive, "IM EINSATZ", GUILayout.Width(130));
            mapShowAvailable = GUILayout.Toggle(mapShowAvailable, "AN WACHE", GUILayout.Width(130));
            mapShowVehicleLabels = GUILayout.Toggle(mapShowVehicleLabels, "NAMEN", GUILayout.Width(100));
            mapShowFocusPanel = GUILayout.Toggle(mapShowFocusPanel, "DETAILS", GUILayout.Width(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("ZOOM", infoStyle, GUILayout.Width(55));
            float z = Mathf.Log(Mathf.Clamp(mapZoom, 8f, 1800f));
            z = GUILayout.HorizontalSlider(z, Mathf.Log(8f), Mathf.Log(1800f), GUILayout.Height(20));
            mapZoom = Mathf.Exp(z);
            GUILayout.Label(Mathf.RoundToInt(mapZoom).ToString(), infoStyle, GUILayout.Width(55));
            GUILayout.EndHorizontal();
            GUILayout.Label(mapInfo + "  |  " + vehicles.Count + " Fahrzeuge  |  Hintergrund nur bei Änderung und nach Intervall", infoStyle);

            float contentWidth = Mathf.Max(400f, windowRect.width - 345f);
            float availableHeight = Mathf.Max(160f, windowRect.height - 480f);
            float listWidth = Mathf.Clamp(contentWidth * 0.30f, 265f, 390f);
            float mapWidth = Mathf.Max(240f, contentWidth - listWidth - 20f);
            GUILayout.BeginHorizontal();
            Rect mapRect = GUILayoutUtility.GetRect(mapWidth, availableHeight,
                GUILayout.Width(mapWidth), GUILayout.Height(availableHeight));
            GUI.DrawTexture(mapRect, panelTexture, ScaleMode.StretchToFill);
            Camera? cam = liveMapCamera;
            if (cam != null && cam.targetTexture != null)
            {
                GUI.DrawTexture(mapRect, cam.targetTexture, ScaleMode.StretchToFill, false);
                for (int i = 0; i < vehicles.Count; i++)
                {
                    VehicleEntry entry = vehicles[i];
                    if (entry.VehicleObject == null || !MapEntryVisible(entry)) continue;
                    Vector3 vp = cam.WorldToViewportPoint(entry.VehicleObject.transform.position);
                    if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                    float x = mapRect.x + vp.x * mapRect.width;
                    float y = mapRect.y + (1f - vp.y) * mapRect.height;
                    string label = MapVehicleLabel(entry);
                    float markerWidth = mapShowVehicleLabels ? Mathf.Clamp(48f + label.Length * 7f, 85f, 170f) : 36f;
                    Rect marker = new Rect(x - markerWidth / 2f, y - 18f, markerWidth, 36f);
                    GUI.DrawTexture(marker, i == mapFocusVehicle ? redTexture : panelTexture);
                    Sprite? sign = GetMapVehicleSign(entry);
                    if (sign != null) DrawMapSign(new Rect(marker.x + 3f, marker.y + 3f, 30f, 30f), sign);
                    Rect clickRect = new Rect(marker.x + (sign == null ? 3f : 35f), marker.y + 1f,
                        marker.width - (sign == null ? 6f : 38f), marker.height - 2f);
                    if (GUI.Button(clickRect, mapShowVehicleLabels ? label : "", mapMarkerStyle)) FocusMapVehicle(i);
                }
                HandleMapMouse(mapRect, cam);
            }
            else GUI.Label(mapRect, "Kartenansicht noch nicht verfügbar", imageBadgeStyle);

            GUILayout.Space(12);
            GUILayout.BeginVertical(GUILayout.Width(listWidth));
            GUILayout.Label("FAHRZEUGE (" + vehicles.Count + ")", headingStyle);
            mapSearch = GUILayout.TextField(mapSearch, GUILayout.Height(30));
            mapListScroll = GUILayout.BeginScrollView(mapListScroll,
                GUILayout.Width(listWidth), GUILayout.Height(Mathf.Max(90f, availableHeight - (mapShowFocusPanel ? 150f : 48f))));
            for (int i = 0; i < vehicles.Count; i++)
            {
                VehicleEntry entry = vehicles[i];
                if (!MapEntryVisible(entry)) continue;
                string label = MapVehicleLabel(entry);
                if (!string.IsNullOrWhiteSpace(mapSearch) &&
                    (entry.VehicleName + " " + entry.SquadName + " " + entry.VehicleType + " " + entry.Status)
                    .IndexOf(mapSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                GUILayout.BeginVertical(panelStyle);
                GUILayout.BeginHorizontal();
                Sprite? sign = GetMapVehicleSign(entry);
                Rect iconRect = GUILayoutUtility.GetRect(35f, 35f, GUILayout.Width(35f), GUILayout.Height(35f));
                if (sign != null) DrawMapSign(iconRect, sign);
                else GUI.Label(iconRect, entry.VehicleType, infoStyle);
                if (GUILayout.Button((i == mapFocusVehicle ? "● " : "") + label,
                    i == mapFocusVehicle ? activeButtonStyle : listStyle)) FocusMapVehicle(i);
                GUILayout.EndHorizontal();
                GUILayout.Label(entry.VehicleName + " | " + entry.VehicleType + " | " + entry.Status, infoStyle);
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            if (mapShowFocusPanel && mapFocusVehicle >= 0 && mapFocusVehicle < vehicles.Count)
            {
                VehicleEntry focus = vehicles[mapFocusVehicle];
                GUILayout.Label("AUSGEWÄHLTES FAHRZEUG", infoStyle);
                GUILayout.Label(MapVehicleLabel(focus) + " | " + focus.Status, infoStyle);
                if (focus.VehicleObject != null)
                {
                    Vector3 pos = focus.VehicleObject.transform.position;
                    GUILayout.Label("X " + pos.x.ToString("F0") + "  Z " + pos.z.ToString("F0") +
                        " | Besatzung: " + focus.Crew.Count, infoStyle);
                }
                if (GUILayout.Button("FAHRZEUGDETAILS ÖFFNEN", smallButtonStyle))
                {
                    selectedTab = 5;
                    vehicleSearch = focus.SquadName;
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Label("Performance: Weltansicht rendert nur bei Bewegung/Zoom und mit Update-Intervall. " +
                "Weltkarte: Scrollrad zoomt, Fahrzeugauswahl zentriert.", infoStyle);
        }

    }
}
