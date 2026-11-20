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
        // ================================================================
        // FIRE CONTROL - VISUAL LIGHTING KONFIGURATION
        // Alle Werte hier oben anpassen. STANDARD = bisheriger Codewert.
        // Diese Einstellungen gelten fuer Unity-Light-Komponenten, NICHT
        // fuer CarLightCurve-Mesh-Emission in PoliceVehicleController.cs.
        // ================================================================

        // LICHTHELLIGKEIT: 1.0 = 100%, 1.9 = 190%, 3.0 = 300%.
        // STANDARD: jeweils 1.9f (vorher gemeinsamer BlueLightBoost).
        private const float BlueLightBoost = 1.9f;    // Blaue Lichtquellen
        private const float RedLightBoost = 1.9f;     // Rote Lichtquellen
        private const float OrangeLightBoost = 1.9f;  // Orange/gelbe Lichtquellen

        // Maximale Lichtintensitaet nach Verstaerkung. STANDARD: 18f.
        // Hoeher = groessere moegliche Helligkeit; kann ueberstrahlen.
        private const float EmergencyLightMaxIntensity = 18f;

        // Anzahl untersuchter passender Warnlampen je Fahrzeug/Scan.
        // STANDARD: 16. Hoeher = mehr Lampen, eventuell mehr Aufwand.
        private const int EmergencyLightMaxPerVehicle = 16;

        // Abstaende der automatischen Scans in Sekunden.
        // STANDARD: Fahrzeuge 6f, Lichtboost 5f, Fehler-Wartezeit 15f.
        // Kleiner = schnellere Erkennung, aber haeufigere Suche.
        private const float VehicleLightScanSeconds = 6f;
        private const float EmergencyBoostScanSeconds = 5f;
        private const float LightScanRetrySeconds = 15f;

        // Grafische Reflexionsverstaerkung. STANDARD: 1.12f (112%).
        // 1.0f = unveraenderte Reflexionsintensitaet.
        private const float GraphicsReflectionDefault = 1.12f;
        // Obergrenze der Reflexionsintensitaet. STANDARD: 2f.
        private const float GraphicsReflectionMax = 2f;

        // Visuals-Menue: Startwert pro Gruppe. STANDARD: 1f (100%).
        // Hinweis: gespeicherte Einstellungen koennen dies ueberschreiben.
        private const float VisualDefaultFactor = 1f;
        // Slider-Minimum/Maximum. STANDARD: 0.5f / 2f (50%-200%).
        private const float VisualSliderMin = 0.5f;
        private const float VisualSliderMax = 2f;
        // Max. Reichweite in Unity-Einheiten. STANDARD: 120f.
        private const float VisualMaxRange = 120f;
        // Max. Intensitaet fuer nicht dynamische Visual-Gruppen.
        // STANDARD: 12f. Fahrzeuglicht-Intensitaeten bleiben spielgesteuert.
        private const float VisualMaxIntensity = 12f;
        // Ab welcher Slider-Aenderung gespeichert wird. STANDARD: 0.005f.
        private const float VisualSliderChangeThreshold = 0.005f;
        // Automatisches erstes Erfassen nach Spielstart. STANDARD: 8f Sek.
        private const float VisualAutoCaptureDelay = 8f;
        // Maximale Tiefe der Pfadpruefung zur Lichtgruppenerkennung.
        // STANDARD: 9 Hierarchieebenen.
        private const int VisualPathDepth = 9;

        // Farb-Erkennung (RGB in Unity, 0.0 bis 1.0).
        // STANDARD: unveraenderte Schwellen aus der bisherigen Klasse.
        // Nur anpassen, wenn Lampen falsch erkannt werden.
        private const float BlueMinChannel = 0.55f;
        private const float BlueOverRedRatio = 1.5f;
        private const float BlueOverGreenRatio = 1.18f;
        private const float RedMinChannel = 0.55f;
        private const float RedOverGreenRatio = 1.5f;
        private const float RedOverBlueRatio = 1.5f;
        private const float OrangeMinRed = 0.65f;
        private const float OrangeMinGreen = 0.20f;
        private const float OrangeGreenRedRatio = 0.85f;
        private const float OrangeMaxBlue = 0.25f;

        // Hallentor-Fix: ausschließlich für den lokalen Avatar während einer Door/Gateway-Interaktion.
        // Fix ist im laufenden Spiel dauerhaft aktiv. Kein Eingriff in Toranimation oder andere KI.

        private static bool gateFixEnabled = true;
        private static bool gateMenuOpen;
        private Harmony? gateHarmony;
        private static PropertyInfo? gateInteractionProperty;
        private static PropertyInfo? gateLocalProperty;
        private static FieldInfo? gateMovementEnabledField;
        private static MethodInfo? gateSetDirection;
        private static float gateNextWarning;

        // Nur Blaulichtstatus-Erkennung fuer die Rettungsgassen-KI.
        // Keine zusaetzlichen Lampen, Reflexe oder Aenderungen an Fahrzeugmodellen.
        private sealed class BlueLightVehicle
        {
            public Component Controller = null!;
            public Transform Vehicle = null!;
            public int EmergencyMask;
        }
        private readonly List<BlueLightVehicle> blueVehicles = new List<BlueLightVehicle>();
        private Type? blueControllerType;
        private PropertyInfo? blueMaskProperty;
        private FieldInfo? blueMaskField;
        private MethodInfo? blueGetLightIndex;
        private float blueNextScan;

        private void ScanBlueLights()
        {
            if (Time.unscaledTime < blueNextScan) return;
            blueNextScan = Time.unscaledTime + VehicleLightScanSeconds;
            try
            {
                if (blueControllerType == null)
                {
                    // Der Controller liegt in einer externen GameUnits/Vehicle-Assembly.
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        blueControllerType = assembly.GetType("com.crenetic.N2019Plugins.Vehicle.CarLightController", false)
                            ?? assembly.GetType("CarLightController", false);
                        if (blueControllerType != null) break;
                    }
                    if (blueControllerType == null) return;
                    blueMaskProperty = blueControllerType.GetProperty("Mask", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    blueMaskField = blueControllerType.GetField("Mask", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    blueGetLightIndex = blueControllerType.GetMethod("GetLightIndex", new Type[] { typeof(string) });
                    if (blueGetLightIndex == null || (blueMaskProperty == null && blueMaskField == null)) return;
                }
                UnityEngine.Object[] controllers = UnityEngine.Object.FindObjectsByType(blueControllerType, FindObjectsSortMode.None);
                foreach (UnityEngine.Object candidate in controllers)
                {
                    Component? component = candidate as Component;
                    if (component == null || !component.gameObject.activeInHierarchy) continue;
                    bool known = false;
                    foreach (BlueLightVehicle entry in blueVehicles)
                        if (entry.Controller == component) { known = true; break; }
                    if (known) continue;
                    int mask = 0;
                    foreach (string name in new [] { "Blaulicht", "Heckblaulicht", "Frontblitzer", "Frontblitzer_Seitlich" })
                    {
                        object? indexObject = blueGetLightIndex!.Invoke(component, new object[] { name });
                        int index = indexObject is int ? (int)indexObject : -1;
                        if (index >= 0 && index < 31) mask |= 1 << index;
                    }
                    if (mask == 0) continue;
                    // Ausschliesslich Fahrzeugposition und originalen Blaulichtstatus erfassen.
                    // Keine Renderer-Abfragen oder zusaetzlichen Unity-Lichtquellen.
                    BlueLightVehicle v = new BlueLightVehicle
                    {
                        Controller = component,
                        Vehicle = component.transform,
                        EmergencyMask = mask
                    };
                    blueVehicles.Add(v);
                }
            }
            catch (Exception)
            {
                ;
                blueNextScan = Time.unscaledTime + LightScanRetrySeconds;
            }
        }

        // Dezente Verstaerkung NUR bereits vorhandener blauer Unity-Lichtquellen.
        // Keine neuen Lights, keine Shader-Aenderungen, keine Kamera-/LOD-Eingriffe.
        private readonly Dictionary<Light, float> blueOriginalIntensities = new Dictionary<Light, float>();
        private float nextBlueLightBoostScan;
        private void UpdateExistingBlueLights()
        {
            if (Time.unscaledTime < nextBlueLightBoostScan) return;
            nextBlueLightBoostScan = Time.unscaledTime + EmergencyBoostScanSeconds;
            try
            {
                // Nur die schon erkannten Einsatzfahrzeuge durchsuchen.
                foreach (BlueLightVehicle vehicle in blueVehicles)
                {
                    if (vehicle.Controller == null || vehicle.Vehicle == null) continue;
                    // Controller kann unterhalb des Fahrzeug-Roots liegen.
                    Transform root = vehicle.Vehicle.root;
                    if (root == null) continue;
                    Light[] lights = root.GetComponentsInChildren<Light>(true);
                    int boosted = 0;
                    foreach (Light light in lights)
                    {
                        if (light == null || boosted >= EmergencyLightMaxPerVehicle) break;
                        Color c = light.color;
                        // Nur eindeutig blaue Lampen, nicht Scheinwerfer, Innenraum oder Umfeldlicht.
                        bool blue = c.b > BlueMinChannel && c.b > c.r * BlueOverRedRatio && c.b > c.g * BlueOverGreenRatio;
                        bool red = c.r > RedMinChannel && c.r > c.g * RedOverGreenRatio && c.r > c.b * RedOverBlueRatio;
                        bool amber = c.r > OrangeMinRed && c.g > OrangeMinGreen && c.g < c.r * OrangeGreenRedRatio && c.b < OrangeMaxBlue;
                        if (!blue && !red && !amber) continue;
                        if (!blueOriginalIntensities.ContainsKey(light))
                        {
                            float original = light.intensity;
                            if (original <= 0f) continue;
                            blueOriginalIntensities.Add(light, original);
                            light.intensity = Mathf.Min(original * (blue ? BlueLightBoost : red ? RedLightBoost : OrangeLightBoost), EmergencyLightMaxIntensity);
                        }
                        boosted++;
                    }
                }
                // Zerstörte Objekte aus dem Cache entfernen.
                var removed = new List<Light>();
                foreach (var pair in blueOriginalIntensities)
                    if (pair.Key == null) removed.Add(pair.Key!);
                foreach (Light light in removed) blueOriginalIntensities.Remove(light);
            }
            catch (Exception)
            {
                ;
                nextBlueLightBoostScan = Time.unscaledTime + LightScanRetrySeconds;
            }
        }

        private void RestoreExistingBlueLights()
        {
            foreach (var pair in blueOriginalIntensities)
            {
                if (pair.Key == null) continue;
                try { pair.Key.intensity = pair.Value; } catch { }
            }
            blueOriginalIntensities.Clear();
        }

        // Behutsames Grafik-Tuning: nutzt vorhandene Unity-Reflexionsdaten.
        // Kein Shader-Austausch, keine neuen Render-Passes, keine zusaetzlichen Kameras.
        private bool graphicsCaptured;
        private float graphicsOriginalReflection;
        private AnisotropicFiltering graphicsOriginalAnisotropy;
        private float graphicsReflectionFactor = GraphicsReflectionDefault;
        private bool graphicsAnisotropy;
        private bool graphicsEnabled = true;

        private void ApplyGraphicsTuning()
        {
            try
            {
                if (!graphicsCaptured)
                {
                    graphicsOriginalReflection = RenderSettings.reflectionIntensity;
                    graphicsOriginalAnisotropy = QualitySettings.anisotropicFiltering;
                    graphicsCaptured = true;
                }
                RenderSettings.reflectionIntensity = graphicsEnabled
                    ? Mathf.Clamp(graphicsOriginalReflection * graphicsReflectionFactor, 0f, GraphicsReflectionMax)
                    : graphicsOriginalReflection;
                // Keine pauschal erzwungene Texturfilterung: optional, da GPU-abhaengig.
                QualitySettings.anisotropicFiltering = graphicsEnabled && graphicsAnisotropy
                    ? AnisotropicFiltering.Enable
                    : graphicsOriginalAnisotropy;
            }
            catch (Exception) { ; }
        }

        private void RestoreGraphicsTuning()
        {
            if (!graphicsCaptured) return;
            try
            {
                RenderSettings.reflectionIntensity = graphicsOriginalReflection;
                QualitySettings.anisotropicFiltering = graphicsOriginalAnisotropy;
            }
            catch { }
        }

        // Visual-Diagnose: bewusst nur auf Knopfdruck, keine dauernden Szenenscans.
        private string visualDiagnosisStatus = "Noch keine Licht-Diagnose ausgefuehrt";
        private void RunVisualLightDiagnosis()
        {
            try
            {
                var report = new StringBuilder();
                report.AppendLine("Fire Control - Licht-Diagnose (nur lesend)");
                report.AppendLine("Zeit: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Szene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                report.AppendLine("ReflectionIntensity: " + RenderSettings.reflectionIntensity.ToString("R"));
                report.AppendLine("AnisotropicFiltering: " + QualitySettings.anisotropicFiltering);
                var lights = Resources.FindObjectsOfTypeAll<Light>();
                int sceneLights = 0, activeLights = 0, blueLights = 0;
                var groups = new Dictionary<string, int>();
                foreach (Light light in lights)
                {
                    if (light == null || !light.gameObject.scene.IsValid()) continue;
                    sceneLights++;
                    if (light.enabled && light.gameObject.activeInHierarchy) activeLights++;
                    Transform parent = light.transform;
                    string hierarchy = "";
                    for (int depth = 0; parent != null && depth < 6; depth++, parent = parent.parent)
                        hierarchy = parent.name + (hierarchy.Length > 0 ? "/" + hierarchy : "");
                    string lower = hierarchy.ToLowerInvariant();
                    Color c = light.color;
                    bool blue = c.b > BlueMinChannel && c.b > c.r * BlueOverRedRatio && c.b > c.g * BlueOverGreenRatio;
                    if (blue) blueLights++;
                    string group = blue ? "Blau" :
                        (lower.Contains("garage") || lower.Contains("halle") || lower.Contains("station") ? "Halle (Namenshinweis)" :
                        (lower.Contains("car") || lower.Contains("vehicle") || lower.Contains("truck") || lower.Contains("fireengine") ? "Fahrzeug (Namenshinweis)" : "Sonstige"));
                    if (!groups.ContainsKey(group)) groups[group] = 0;
                    groups[group]++;
                    report.AppendLine("LIGHT | " + group + " | " + hierarchy +
                        " | scene=" + light.gameObject.scene.name +
                        " | enabled=" + light.enabled + " | active=" + light.gameObject.activeInHierarchy +
                        " | type=" + light.type + " | intensity=" + light.intensity.ToString("R") +
                        " | range=" + light.range.ToString("R") +
                        " | color=" + c.ToString() + " | shadows=" + light.shadows);
                }
                report.Insert(0, "Gefundene Szenen-Lichter: " + sceneLights + "; aktiv: " + activeLights +
                    "; blau (Farbheuristik): " + blueLights + Environment.NewLine);
                foreach (var entry in groups) report.AppendLine("GRUPPE | " + entry.Key + " | " + entry.Value);
                report.AppendLine("Hinweis: Gruppierung nach Objektnamen/Farbe ist vorlaeufig und keine sichere Zuordnung.");
                string folder = Path.Combine(Paths.ConfigPath, "FireControl");
                Directory.CreateDirectory(folder);
                string filename = Path.Combine(folder, "Licht_Diagnose.txt");
                ;
                visualDiagnosisStatus = "Gefunden: " + sceneLights + " Lichter (" + activeLights + " aktiv, " + blueLights + " blau). Bericht: " + filename;
                statusMessage = "Licht-Diagnose gespeichert";
                ;
            }
            catch (Exception ex)
            {
                visualDiagnosisStatus = "Diagnosefehler: " + ex.Message;
                ;
            }
        }

        // Visuals 1.0: Scan ausschliesslich per Knopfdruck; keine neuen Lampen.
        // Fahrzeuglichter: nur Reichweite aendern, da das Spiel intensity selbst animiert.
        private static readonly string[] visualNames = { "Fahrzeughalle", "Blaulicht (Fahrzeuge)",
            "Abblendlicht", "Scheinwerfer", "Umfeldbeleuchtung", "Helmlampen", "Strassenlaternen", "Alle weiteren Lichter" };
        private readonly float[] visualFactors = { VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor, VisualDefaultFactor };
        private readonly bool[] visualActive = { true, true, true, true, true, true, true, true };
        private readonly int[] visualCounts = new int[8];
        private bool visualAutoCaptured;
        private float visualAutoCaptureAt = VisualAutoCaptureDelay;
        private sealed class VisualLightEntry
        {
            public Light Light = null!;
            public float Intensity;
            public float Range;
            public int Group;
        }
        private readonly Dictionary<Light, VisualLightEntry> visualLights = new Dictionary<Light, VisualLightEntry>();
        private string visualControlStatus = "Noch keine Lichter erfasst";
        private static int VisualGroup(Light light)
        {
            string path = "";
            Transform t = light.transform;
            for (int i = 0; t != null && i < VisualPathDepth; i++, t = t.parent)
                path = t.name.ToLowerInvariant() + "/" + path;
            string scene = light.gameObject.scene.name.ToLowerInvariant();
            if (scene == "world_firestation" && path.Contains("light_vehicledepot") &&
                !path.Contains("light_alarm") && !path.Contains("light_gate")) return 0;
            if (path.Contains("lightsignals/controllable/blaulicht/emergencylightsource")) return 1;
            if (path.Contains("lightsignals/controllable/carlight_abblendlicht/")) return 2;
            if (path.Contains("lightsignals/controllable/carlight_scheinwerfer/")) return 3;
            if (path.Contains("lightsignals/controllable/carlight_umgebungsbeleuchtung/")) return 4;
            if (path.Contains("helmetlamp")) return 5;
            if (path.Contains("streetlight_02_orange")) return 6;
            return 7; // Auch bisher nicht erkannte und deaktivierte Szenen-Lichter erfassen.
        }
        private void VisualCaptureLights()
        {
            try
            {
                // Vor erneutem Erfassen die alten Werte wiederherstellen.
                VisualRestoreLights();
                Array.Clear(visualCounts, 0, visualCounts.Length);
                foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                {
                    if (light == null || !light.gameObject.scene.IsValid()) continue;
                    int group = VisualGroup(light);
                    if (group < 0) continue;
                    // Deaktivierte GameObjects bleiben erhalten; die Spielsteuerung wird nicht umgangen.
                    visualLights[light] = new VisualLightEntry { Light = light,
                        Intensity = light.intensity, Range = light.range, Group = group };
                    visualCounts[group]++;
                }
                VisualApplyLights();
                visualControlStatus = visualLights.Count + " Szenen-Lichter erfasst (auch deaktivierte)";
                ;
            }
            catch (Exception ex) { visualControlStatus = "Fehler: " + ex.Message; ; }
        }
        private void VisualApplyLights()
        {
            foreach (VisualLightEntry entry in visualLights.Values)
            {
                if (entry.Light == null) continue;
                try
                {
                    float factor = visualActive[entry.Group] ? visualFactors[entry.Group] : 1f;
                    entry.Light.range = Mathf.Clamp(entry.Range * factor, 0f, VisualMaxRange);
                    // Fahrzeuglampen 1-4 werden dynamisch vom Spiel gesteuert.
                    // Daher hier niemals intensity setzen oder enabled umschalten.
                    if (entry.Group == 0 || entry.Group == 5 || entry.Group == 6 || entry.Group == 7)
                        entry.Light.intensity = Mathf.Clamp(entry.Intensity * factor, 0f, VisualMaxIntensity);
                }
                catch { }
            }
        }
        private void VisualRestoreLights()
        {
            foreach (VisualLightEntry entry in visualLights.Values)
            {
                if (entry.Light == null) continue;
                try
                {
                    entry.Light.range = entry.Range;
                    if (entry.Group == 0 || entry.Group == 5 || entry.Group == 6 || entry.Group == 7)
                        entry.Light.intensity = entry.Intensity;
                }
                catch { }
            }
            visualLights.Clear();
        }
        private void DrawVisualControls()
        {
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("VISUALS & LICHTSTEUERUNG", headingStyle);
            GUILayout.Label("Alle Szenen-Lichter, auch derzeit ausgeschaltete, werden erfasst. Automatisch einmal beim Start; neue Fahrzeuge/Szenen manuell aktualisieren. Fahrzeugregler aendern nur Reichweite, damit Lichtschalter und Blinkmuster erhalten bleiben.", infoStyle);
            if (GUILayout.Button("LICHTER ERFASSEN / NEUE SZENE AKTUALISIEREN", buttonStyle)) VisualCaptureLights();
            GUILayout.Label(visualControlStatus, infoStyle);
            for (int i = 0; i < visualNames.Length; i++)
            {
                GUILayout.Space(7f);
                bool enabled = GUILayout.Toggle(visualActive[i], " " + visualNames[i] + " (" + visualCounts[i] + " Lichter)");
                if (enabled != visualActive[i]) { visualActive[i] = enabled; VisualApplyLights(); SaveAllSettings(true); }
                GUILayout.Label((i >= 1 && i <= 4 ? "Reichweite" : "Helligkeit und Reichweite") + ": " +
                    Mathf.RoundToInt(visualFactors[i] * 100f) + "%", textStyle);
                float factor = GUILayout.HorizontalSlider(visualFactors[i], VisualSliderMin, VisualSliderMax);
                if (Mathf.Abs(factor - visualFactors[i]) > VisualSliderChangeThreshold)
                {
                    visualFactors[i] = factor;
                    VisualApplyLights();
                    SaveAllSettings(true);
                }
            }
            GUILayout.Space(12f);
            if (GUILayout.Button("ALLE LICHTER AUF ORIGINAL ZURUECKSETZEN", buttonStyle))
            {
                VisualRestoreLights();
                for (int i = 0; i < visualFactors.Length; i++) { visualFactors[i] = VisualDefaultFactor; visualActive[i] = true; visualCounts[i] = 0; }
                visualControlStatus = "Originalwerte wiederhergestellt. Zum erneuten Einstellen Lichter erfassen.";
                SaveAllSettings(true);
            }
            GUILayout.Label("Einstellungen einschliesslich 200% werden dauerhaft in Einstellungen.json gespeichert. Neue Fahrzeuge nach Spawn manuell erfassen. Andere Lichter koennen ebenfalls dynamisch vom Spiel gesteuert werden; mehr Licht kann FPS kosten.", infoStyle);
            GUILayout.EndVertical();
        }

        // Diagnostiziert den Pfad vom Bundle bis zur originalen CarLightCurve-Zuordnung.
        // Ausschliesslich lesend; weder Renderer noch Lichtsteuerung werden veraendert.
    }
}
