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

        private string fcDoorLightDiagnosisStatus = "Noch keine POL_FC-Lichtdiagnose";
        private static void FCDiagGroup(StringBuilder report, string label, Transform root, string group)
        {
            report.AppendLine("=== " + label + " / " + group + " ===");
            if (root == null) { report.AppendLine("ROOT FEHLT"); return; }
            Transform visuals = root.Find("Visuals");
            if (visuals == null) { report.AppendLine("VISUALS FEHLT"); return; }
            var nodes = FCFindLightGroups(visuals, group);
                report.AppendLine("Gruppen: " + nodes.Count + " | Suchname getrimmt: [" + group.Trim() + "]");
                if (nodes.Count == 0)
                    report.AppendLine("KEINE GRUPPE GEFUNDEN: Bundle-Version, uebernommene Visuals und Leerzeichen pruefen");
            foreach (Transform node in nodes)
            {
                report.AppendLine("GRUPPE | " + FCNativePath(node, root) +
                    " | activeSelf=" + node.gameObject.activeSelf +
                    " | activeInHierarchy=" + node.gameObject.activeInHierarchy);
                foreach (Renderer r in node.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    report.AppendLine("RENDERER | " + FCNativePath(r.transform, root) +
                        " | enabled=" + r.enabled +
                        " | activeSelf=" + r.gameObject.activeSelf +
                        " | activeInHierarchy=" + r.gameObject.activeInHierarchy +
                        " | mesh=" + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "KEIN MESH FILTER") +
                        " | materials=" + r.sharedMaterials.Length);
                }
            }
        }

        private void FCDiagnoseDoorLights()
        {
            try
            {
                var report = new StringBuilder();
                report.AppendLine("FIRE CONTROL 3.5.0 - POL_FC TUER-/HECKBLITZER DIAGNOSE");
                report.AppendLine("Zeit: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Szene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                string[] groups = { "L_emergency_F_blue", "L_emergency_F_2_blue", "L_emergency_F_3_blue", "L_emergency_B_2_blue" };
                if (fcVehiclePrefab == null)
                    report.AppendLine("BUNDLE-PREFAB NICHT GELADEN: zuerst POL_FC im Spiel laden");
                else foreach (string group in groups)
                    FCDiagGroup(report, "ASSETBUNDLE POL_FC", fcVehiclePrefab.transform, group);

                Type curveType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightCurve");
                int count = 0;
                foreach (FCRealPoliceFlash entry in fcRealPolice)
                {
                    if (entry.Root == null || entry.Controller == null) continue;
                    count++;
                    report.AppendLine("\n=== LIVE POLIZEIFAHRZEUG " + count + " : " + entry.Root.name + " ===");
                    foreach (string group in groups) FCDiagGroup(report, "LIVE", entry.Root, group);
                    FieldInfo? tableField = FCNativeField(entry.Controller.GetType(), "carLights");
                    FieldInfo? meshesField = curveType == null ? null : FCNativeField(curveType, "lightMeshes");
                    var table = tableField == null ? null : tableField.GetValue(entry.Controller) as System.Collections.IDictionary;
                    if (table == null || meshesField == null)
                    {
                        report.AppendLine("CARLIGHT-API FEHLT: carLights oder lightMeshes nicht lesbar");
                        continue;
                    }
                    foreach (System.Collections.DictionaryEntry pair in table)
                    {
                        if (!(pair.Value is Array array)) continue;
                        foreach (object obj in array)
                        {
                            Component? component = obj as Component;
                            if (component == null) continue;
                            Renderer[]? meshes = meshesField.GetValue(component) as Renderer[];
                            if (meshes == null) continue;
                            foreach (Renderer renderer in meshes)
                            {
                                if (renderer == null) continue;
                                string path = FCNativePath(renderer.transform, entry.Root);
                                if (path.IndexOf("emergency", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    path.IndexOf("frontblitz", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                report.AppendLine("ZUGEORDNET | group=" + pair.Key +
                                    " | curve=" + component.name +
                                    " | renderer=" + path +
                                    " | enabled=" + renderer.enabled +
                                    " | active=" + renderer.gameObject.activeInHierarchy);
                            }
                        }
                    }
                }
                report.AppendLine("LIVE POL_FC ANZAHL: " + count);
                report.AppendLine("HINWEIS: Wenn BUNDLE einen Renderer hat, LIVE aber nicht, liegt das Problem bei FCMergeVisualTree.");
                report.AppendLine("Wenn LIVE den Renderer hat, aber keine ZUGEORDNET-Zeile, liegt das Problem bei FCInstallNativePoliceLights.");
                report.AppendLine("Wenn zugeordnet aber active=false: Elternobjekte und Tueranimation pruefen.");
                string folder = Path.Combine(Paths.ConfigPath, "FireControl");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "POL_FC_Tuerblitzer_Diagnose.txt");
                ;
                fcDoorLightDiagnosisStatus = "Diagnose gespeichert: " + file + " | Fahrzeuge=" + count;
                ;
            }
            catch (Exception ex)
            {
                fcDoorLightDiagnosisStatus = "Diagnose fehlgeschlagen: " + ex.Message;
                ;
            }
        }

        private Vector2 graphicsTabScroll;
        private void DrawGraphicsTab()
        {
            graphicsTabScroll = GUILayout.BeginScrollView(graphicsTabScroll, false, true);
            GUILayout.Label("GRAFIK & REFLEXIONEN", headingStyle);
            GUILayout.Space(6f);
            if (GUILayout.Button("POL_FC TUERBLITZER DIAGNOSE SPEICHERN", buttonStyle)) FCDiagnoseDoorLights();
            GUILayout.Label(fcDoorLightDiagnosisStatus, infoStyle);
            GUILayout.Space(8f);
            DrawVisualControls();
            GUILayout.Space(12f);
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("LICHT-DIAGNOSE (NUR LESEN)", textStyle);
            GUILayout.Label("Erfasst vorhandene Unity-Lichter einmalig. Aendert keine Lampen und fuehrt keine Dauersuche aus.", infoStyle);
            if (GUILayout.Button("LICHTER JETZT ANALYSIEREN", buttonStyle)) RunVisualLightDiagnosis();
            GUILayout.Label(visualDiagnosisStatus, infoStyle);
            GUILayout.Space(8f);
            if (GUILayout.Button("POL_FC TUERBLITZER DIAGNOSE SPEICHERN", buttonStyle)) FCDiagnoseDoorLights();
            GUILayout.Label(fcDoorLightDiagnosisStatus, infoStyle);
            GUILayout.EndVertical();
            GUILayout.Space(12);
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("SANFTES GRAFIK-TUNING", textStyle);
            GUILayout.Label("Nutzt vorhandene Reflection-Probes und Shader des Spiels. " +
                "Keine neuen Echtzeitspiegelungen oder zusaetzlichen Renderdurchlaeufe.", infoStyle);
            bool enabled = GUILayout.Toggle(graphicsEnabled, " Reflexions-Tuning aktiv");
            if (enabled != graphicsEnabled) { graphicsEnabled = enabled; ApplyGraphicsTuning(); }
            GUILayout.Space(10);
            GUILayout.Label("Reflexionsintensitaet: " + graphicsReflectionFactor.ToString("0.00") + "x", textStyle);
            float factor = GUILayout.HorizontalSlider(graphicsReflectionFactor, 1.0f, 1.35f);
            if (Mathf.Abs(factor - graphicsReflectionFactor) > 0.005f)
            {
                graphicsReflectionFactor = factor;
                ApplyGraphicsTuning();
            }
            GUILayout.Label("Empfohlen: 1.10 bis 1.20. Hoehere Werte koennen kuenstlich aussehen.", infoStyle);
            GUILayout.Space(10);
            bool anisotropy = GUILayout.Toggle(graphicsAnisotropy, " Schaerfere schräge Texturen (optional, GPU-abhaengig)");
            if (anisotropy != graphicsAnisotropy) { graphicsAnisotropy = anisotropy; ApplyGraphicsTuning(); }
            GUILayout.Space(12);
            if (GUILayout.Button("GRAFIK AUF ORIGINAL ZURUECKSETZEN", buttonStyle))
            {
                graphicsEnabled = false;
                graphicsAnisotropy = false;
                graphicsReflectionFactor = 1.12f;
                RestoreGraphicsTuning();
            }
            GUILayout.Space(10);
            GUILayout.Label("Hinweis: Das ist keine Shader-Neuentwicklung. Mehr echte Spiegelungen " +
                "auf Asphalt, Fahrzeuglack und Glas erfordern passende Shader, Materialien " +
                "und oft GPU-Leistung. FPS-Neutralitaet ist nicht garantiert.", infoStyle);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

    }
}
