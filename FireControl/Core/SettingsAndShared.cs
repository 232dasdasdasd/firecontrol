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

        private string SettingsFile => Path.Combine(Paths.ConfigPath, "FireControl", "Einstellungen.json");
        private string settingsLastSnapshot = "";
        private float settingsNextCheck;
        private bool settingsLoading;
        private bool settingsEnvironmentPending;
        private PersistentSettings persistent = new PersistentSettings();

        private PersistentSettings CaptureSettings()
        {
            return new PersistentSettings
            {
                playerMovementFactor = playerMovementFactor,
                manualBlueCurveMode = manualBlueCurveMode,
                trafficCars = trafficCars, trafficPedestrians = trafficPedestrians,
                trafficYieldEnabled = trafficYieldEnabled,
                graphicsEnabled = graphicsEnabled, graphicsAnisotropy = graphicsAnisotropy,
                graphicsReflectionFactor = graphicsReflectionFactor,
                visualFactors = (float[])visualFactors.Clone(), visualActive = (bool[])visualActive.Clone(),
                mapVisualMode = mapVisualMode, mapRenderQuality = mapRenderQuality,
                mapRenderSpeed = mapRenderSpeed, mapFollow = mapFollow,
                mapShowAllVehicles = mapShowAllVehicles, mapShowActive = mapShowActive,
                mapShowAvailable = mapShowAvailable, mapShowVehicleLabels = mapShowVehicleLabels,
                mapShowFocusPanel = mapShowFocusPanel, mapZoom = mapZoom,
                selectedWeather = selectedWeather ?? "",
                hasTimeSpeed = persistent.hasTimeSpeed, timeSpeed = persistent.timeSpeed,
                hasDateTime = persistent.hasDateTime,
                selectedDateTime = persistent.selectedDateTime ?? ""
            };
        }

        private static string SerializeSettings(PersistentSettings settings)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PersistentSettings)).WriteObject(stream, settings);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private void LoadAllSettings()
        {
            settingsLoading = true;
            try
            {
                if (File.Exists(SettingsFile))
                {
                    using (var input = File.OpenRead(SettingsFile))
                        persistent = (PersistentSettings?)new DataContractJsonSerializer(
                            typeof(PersistentSettings)).ReadObject(input) ?? new PersistentSettings();

                    playerMovementFactor = Mathf.Clamp(persistent.playerMovementFactor, 0.5f, 2f);
                    manualBlueCurveMode = Mathf.Clamp(persistent.manualBlueCurveMode, 0, 4);
                    trafficCars = Mathf.Clamp01(persistent.trafficCars);
                    trafficPedestrians = Mathf.Clamp01(persistent.trafficPedestrians);
                    trafficYieldEnabled = persistent.trafficYieldEnabled;
                    graphicsEnabled = persistent.graphicsEnabled;
                    graphicsAnisotropy = persistent.graphicsAnisotropy;
                    graphicsReflectionFactor = Mathf.Clamp(persistent.graphicsReflectionFactor, 1f, 1.35f);
                    if (persistent.visualFactors != null)
                        for (int i = 0; i < Mathf.Min(visualFactors.Length, persistent.visualFactors.Length); i++)
                            visualFactors[i] = Mathf.Clamp(persistent.visualFactors[i], 0.5f, 2f);
                    if (persistent.visualActive != null)
                        for (int i = 0; i < Mathf.Min(visualActive.Length, persistent.visualActive.Length); i++)
                            visualActive[i] = persistent.visualActive[i];
                    mapVisualMode = Mathf.Clamp(persistent.mapVisualMode, 0, 1);
                    mapRenderQuality = Mathf.Clamp(persistent.mapRenderQuality, 0, 2);
                    mapRenderSpeed = Mathf.Clamp(persistent.mapRenderSpeed, 0, 2);
                    mapFollow = persistent.mapFollow;
                    mapShowAllVehicles = persistent.mapShowAllVehicles;
                    mapShowActive = persistent.mapShowActive;
                    mapShowAvailable = persistent.mapShowAvailable;
                    mapShowVehicleLabels = persistent.mapShowVehicleLabels;
                    mapShowFocusPanel = persistent.mapShowFocusPanel;
                    mapZoom = Mathf.Clamp(persistent.mapZoom, 8f, 1800f);
                    selectedWeather = persistent.selectedWeather ?? "";
                    settingsEnvironmentPending = persistent.hasTimeSpeed ||
                        persistent.hasDateTime || !string.IsNullOrEmpty(selectedWeather);
                    ;
                }
                settingsLastSnapshot = SerializeSettings(CaptureSettings());
            }
            catch (Exception)
            {
                ;
                settingsLastSnapshot = SerializeSettings(CaptureSettings());
            }
            finally { settingsLoading = false; }
        }

        private void SaveAllSettings(bool force = false)
        {
            if (settingsLoading) return;
            try
            {
                PersistentSettings current = CaptureSettings();
                string json = SerializeSettings(current);
                if (!force && json == settingsLastSnapshot) return;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                string temp = SettingsFile + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(SettingsFile))
                {
                    // Backup der letzten funktionierenden Konfiguration.
                    File.Copy(SettingsFile, SettingsFile + ".bak", true);
                    File.Delete(SettingsFile);
                }
                File.Move(temp, SettingsFile);
                settingsLastSnapshot = json;
                persistent = current;
                
            }
            catch (Exception) { ; }
        }

        private void RestoreEnvironmentSettings()
        {
            if (!settingsEnvironmentPending) return;
            try
            {
                var environment = GetEnvironment();
                if (environment == null || !environment.IsServer) return;
                if (persistent.hasDateTime &&
                    DateTime.TryParseExact(persistent.selectedDateTime, "yyyy-MM-ddTHH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime date))
                    environment.SetDateTime(date);
                if (!string.IsNullOrEmpty(persistent.selectedWeather))
                {
                    IEnviroment control = environment;
                    control.SetWeather(persistent.selectedWeather);
                }
                if (persistent.hasTimeSpeed)
                    environment.SetTimeSpeed(persistent.timeSpeed);
                settingsEnvironmentPending = false;
                ;
            }
            catch (Exception)
            {
                ;
            }
        }

        // Die Original-API CarLightCurve.AssignCurve(AnimationCurve) aus Vehicle.dll.
        // Wichtig: Weder CarLight.enabled noch CarLightController.UpdateRemote/Mask anfassen.
        // Somit bleibt Blaulicht beim Spielstart AUS, bis das Spiel es selbst einschaltet.
        private int manualBlueCurveMode = 1;
        private readonly Dictionary<Component, AnimationCurve> manualOriginalCurves =
            new Dictionary<Component, AnimationCurve>();
        private static readonly string[] manualCurveNames = {
            "STANDBY - dauerhaft an", "DOPPELBLITZ", "STROBOSKOP", "DREIFACHBLITZ", "LICHTWELLE"
        };
        private string manualCurveResult = "Noch keine Kurve angewendet";
        private Type? manualCurveType;
        private MethodInfo? manualAssignCurve;
        private FieldInfo? manualEmissionCurve;
        private PropertyInfo? manualLightTypeProperty;
        private FieldInfo? manualLightTypeField;

        private static AnimationCurve ManualCurve(params float[] values)
        {
            var keys = new Keyframe[values.Length / 2];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new Keyframe(values[i * 2], values[i * 2 + 1]);
            var curve = new AnimationCurve(keys);
            curve.preWrapMode = WrapMode.Loop;
            curve.postWrapMode = WrapMode.Loop;
            return curve;
        }

        private static AnimationCurve MakeManualBlueCurve(int mode, int phase)
        {
            if (mode == 0)
            {
                // Constant 1, even after the last keyframe.
                var steady = ManualCurve(0f, 1f, 1f, 1f);
                steady.preWrapMode = WrapMode.ClampForever;
                steady.postWrapMode = WrapMode.ClampForever;
                return steady;
            }
            if (mode == 1)
                return ManualCurve(0f, 0f, .03f, 1f, .07f, 0f,
                    .12f, 1f, .16f, 0f, .55f, 0f, .58f, 1f, .62f, 0f,
                    .67f, 1f, .71f, 0f, 1f, 0f);
            if (mode == 2)
                return ManualCurve(0f, 0f, .025f, 1f, .055f, 0f,
                    .08f, 1f, .11f, 0f, .135f, 1f, .165f, 0f,
                    .19f, 1f, .22f, 0f, .45f, 0f);
            if (mode == 3)
                return ManualCurve(0f, 0f, .045f, 1f, .095f, 0f,
                    .17f, 1f, .22f, 0f, .295f, 1f, .345f, 0f, .75f, 0f);
            // Alle Lichtgruppen bleiben aktiv; lediglich die Kurve ist versetzt.
            float shift = (phase % 4) * .16f;
            return ManualCurve(0f, 0f, .10f + shift, 0f,
                .12f + shift, 1f, .24f + shift, 1f,
                .28f + shift, 0f, 1f, 0f);
        }

        private bool IsManualBlueType(Component light)
        {
            try
            {
                object? raw = manualLightTypeProperty?.GetValue(light, null)
                    ?? manualLightTypeField?.GetValue(light);
                string type = (raw == null ? "" : raw.ToString() ?? "").ToLowerInvariant();
                return type.Contains("blaulicht") || type.Contains("blitzer") ||
                       type.Contains("blue") || type.Contains("beacon");
            }
            catch { return false; }
        }

        private void ApplyManualBlueCurves()
        {
            try
            {
                if (manualCurveType == null)
                {
                    manualCurveType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightCurve");
                    if (manualCurveType == null)
                    {
                        manualCurveResult = "CarLightCurve in Vehicle.dll nicht gefunden";
                        return;
                    }
                    manualAssignCurve = AccessTools.Method(manualCurveType, "AssignCurve",
                        new[] { typeof(AnimationCurve) });
                    manualEmissionCurve = AccessTools.Field(manualCurveType, "emissionCurve");
                    manualLightTypeProperty = AccessTools.Property(manualCurveType, "LightType");
                    manualLightTypeField = AccessTools.Field(manualCurveType, "LightType");
                }
                if (manualAssignCurve == null || manualEmissionCurve == null)
                {
                    manualCurveResult = "CarLightCurve-Schnittstelle unvollstaendig";
                    return;
                }
                // Einmaliger Scan NUR beim Klicken, niemals in Update().
                var all = UnityEngine.Object.FindObjectsByType(manualCurveType, FindObjectsSortMode.None);
                int changed = 0, found = 0;
                var curves = new AnimationCurve[4];
                for (int i = 0; i < 4; i++)
                    curves[i] = MakeManualBlueCurve(manualBlueCurveMode, i);
                foreach (UnityEngine.Object obj in all)
                {
                    Component? light = obj as Component;
                    if (light == null || !IsManualBlueType(light)) continue;
                    found++;
                    if (!manualOriginalCurves.ContainsKey(light))
                    {
                        var original = manualEmissionCurve.GetValue(light) as AnimationCurve;
                        if (original != null) manualOriginalCurves[light] = original;
                    }
                    // Der Spielcontroller schaltet die Lampe wie bisher EIN/AUS.
                    // Wir tauschen ausschliesslich die Emissionskurve aus.
                    int phase = Mathf.Abs(light.GetInstanceID() % 4);
                    manualAssignCurve.Invoke(light, new object[] { curves[phase] });
                    changed++;
                }
                manualCurveResult = changed + " von " + found +
                    " vorhandenen Blaulicht-Kurven angepasst (nur Emission)";
                statusMessage = manualCurveResult;
                ;
                SaveAllSettings(true);
            }
            catch (Exception ex)
            {
                manualCurveResult = "Fehler: " + ex.Message;
                ;
            }
        }

        private void RestoreManualBlueCurves()
        {
            if (manualAssignCurve == null) return;
            foreach (var item in manualOriginalCurves)
            {
                if (item.Key == null || item.Value == null) continue;
                try { manualAssignCurve.Invoke(item.Key, new object[] { item.Value }); }
                catch { }
            }
            manualOriginalCurves.Clear();
        }

        private void DrawManualBlueCurvesTab()
        {
            GUILayout.Label("BLAULICHT-KURVEN", headingStyle);
            GUILayout.Label("Nur Blinkkurven aendern. Das Spiel schaltet Blaulicht weiterhin selbst EIN/AUS.", infoStyle);
            GUILayout.Space(12f);
            for (int i = 0; i < manualCurveNames.Length; i++)
            {
                if (GUILayout.Button((manualBlueCurveMode == i ? "[X] " : "[ ] ") +
                    manualCurveNames[i], buttonStyle))
                {
                    manualBlueCurveMode = i;
                    ApplyManualBlueCurves(); // Sofort beim Klicken, nicht zeitverzoegert.
                }
            }
            GUILayout.Space(10f);
            if (GUILayout.Button("AUF ALLE AKTUELLEN FAHRZEUGE ANWENDEN", buttonStyle))
                ApplyManualBlueCurves();
            if (GUILayout.Button("ORIGINAL-BLINKKURVEN WIEDERHERSTELLEN", buttonStyle))
            {
                RestoreManualBlueCurves();
                manualCurveResult = "Originalkurven wiederhergestellt";
            }
            GUILayout.Label(manualCurveResult, infoStyle);
            GUILayout.Label("Neue Fahrzeuge nach einem Szenenwechsel: einmal 'Anwenden' klicken. " +
                "Absichtlich kein Dauerscan, um Ruckler zu vermeiden.", infoStyle);
        }

        // Fire Control: exportiertes Polizei-AssetBundle (erster Test: Handschellen).
        // Das Bundle wird nur auf Knopfdruck geladen; keine Szenenobjekte werden ungefragt veraendert.
        private AssetBundle? policeBundle;
        private AssetBundle? policeAvatarBundle; // Abhaengigkeit: Meshes aus default_avatars
        private bool policeAvatarBundleOwned; // Nur selbst geladene Bundles entladen
        private GameObject? policeEquipmentPrefab;
        private readonly List<GameObject> policeTestInstances = new List<GameObject>();
        private string policeBundleStatus = "Bundle noch nicht geprueft";

        private string PoliceBundlePath => string.Empty; // Altes Bundle wird nicht mehr verwendet.
        private string PoliceAvatarBundlePath => Path.Combine(Paths.PluginPath, "FireControl", "default_avatars");

        private void LoadPoliceBundle()
        {
            // Polizei-Ausrüstung vorläufig deaktiviert. Kein separates Bundle laden.
            policeBundleStatus = "Polizei-Ausrüstung vorübergehend deaktiviert";
        }

        private static Transform? FindPoliceBone(Transform root, string boneName)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, boneName, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private void TestPoliceHandcuffs()
        {
            if (policeEquipmentPrefab == null) LoadPoliceBundle();
            if (policeEquipmentPrefab == null) return;
            GameObject? instance = null;
            try
            {
                // Nur echte Szeneninstanzen mit einem passenden Polizeiskelett verwenden.
                Transform? target = null;
                foreach (GameObject root in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (root == null || !root.scene.IsValid() || root.transform.parent != null) continue;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || !t.gameObject.activeInHierarchy) continue;
                        string n = t.name.ToLowerInvariant();
                        if (!(n.Contains("polizist") || n.Contains("policeman") || n.Contains("police"))) continue;
                        if (FindPoliceBone(t, "fireman01_Hips") != null)
                        {
                            target = t;
                            break;
                        }
                    }
                    if (target != null) break;
                }
                if (target == null)
                {
                    policeBundleStatus = "Kein aktiver Polizist mit fireman01_Hips-Skelett gefunden.";
                    statusMessage = policeBundleStatus;
                    ;
                    return;
                }

                Camera? policeCamera = Camera.main;
                ;

                // Alte Testobjekte entfernen, damit sie nicht uebereinander liegen.
            RemovePoliceTestInstances();
                instance = Instantiate(policeEquipmentPrefab, target);
                instance.name = "FC_Handcuffs_Test";
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                // Diagnose der Instanz VOR jeglicher Aenderung an Renderern und Bones.
                foreach (SkinnedMeshRenderer r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (r == null || !r.name.StartsWith("polizei_handcuffs_LOD", StringComparison.OrdinalIgnoreCase)) continue;
                    Mesh mesh = r.sharedMesh;
                    ;
                }

                // Alle Meshes der vollstaendigen Prefab-Kopie zunaechst verstecken.
                Renderer[] allRenderers = instance.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in allRenderers)
                    if (r != null) r.enabled = false;

                int found = 0, mapped = 0, missing = 0;
                foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || !string.Equals(smr.name, "polizei_handcuffs_LOD0", StringComparison.OrdinalIgnoreCase))
                        continue;
                    found++;
                    Transform[] oldBones = smr.bones;
                    Transform[] targetBones = new Transform[oldBones.Length];
                    bool valid = oldBones.Length > 0;
                    string problem = "";
                    for (int i = 0; i < oldBones.Length; i++)
                    {
                        Transform? original = oldBones[i];
                        Transform? bone = original == null ? null : FindPoliceBone(target, original.name);
                        if (bone == null)
                        {
                            valid = false;
                            if (problem.Length == 0)
                                problem = "Bone " + i + " fehlt: " + (original == null ? "null" : original.name);
                        }
                        else targetBones[i] = bone;
                    }
                    string rootName = smr.rootBone != null ? smr.rootBone.name : "(null)";
                    Transform? targetRoot = FindPoliceBone(target, rootName);
                    if (targetRoot == null)
                    {
                        valid = false;
                        if (problem.Length == 0) problem = "Root Bone fehlt: " + rootName;
                    }
                    if (!valid)
                    {
                        missing++;
                        ;
                        continue;
                    }

                    smr.bones = targetBones;
                    smr.rootBone = targetRoot;
                    smr.updateWhenOffscreen = true;
                    // Nur die Vorfahren des Handschellen-Meshes aktivieren.
                    Transform? parent = smr.transform;
                    while (parent != null && parent != target)
                    {
                        parent.gameObject.SetActive(true);
                        parent = parent.parent;
                    }
                    smr.enabled = true;
                    mapped++;
                    ;
                    for (int boneIndex = 0; boneIndex < Mathf.Min(5, targetBones.Length); boneIndex++)
                    {
                        ;
                    }
                    ;
                    ;
                }

                policeTestInstances.Add(instance);
                policeBundleStatus = "Handschellen-Test: " + found + " LOD0 gefunden, " + mapped +
                    " aktiviert, " + missing + " ohne passende Bones; " +
                    (allRenderers.Length - mapped) + " andere Renderer ausgeblendet.";
                ;
                statusMessage = policeBundleStatus;
            }
            catch (Exception ex)
            {
                if (instance != null) Destroy(instance);
                policeBundleStatus = "Handschellen-Test fehlgeschlagen: " + ex.Message;
                ;
                statusMessage = policeBundleStatus;
            }
        }

        private void RemovePoliceTestInstances()
        {
            foreach (GameObject instance in policeTestInstances)
                if (instance != null) Destroy(instance);
            policeTestInstances.Clear();
            policeBundleStatus = "Testobjekte entfernt";
            statusMessage = policeBundleStatus;
        }

        // Automatische Polizeiausruestung: nur vorhandene Renderer aus dem Bundle, jeweils LOD0.
        // Jeder Polizist bekommt genau eine Ausruestungs-Kopie. Die Spielmodelle bleiben unberuehrt.
    }
}
