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

        private sealed class PoliceOption
        {
            public string Label;
            public string[] Aliases;
            public bool Enabled;
            public PoliceOption(string label, bool enabled, params string[] aliases)
            { Label = label; Enabled = enabled; Aliases = aliases; }
        }

        // Ein Eintrag pro Gegenstand. Keine doppelte Weste / Lederhandschuhe.
        private readonly PoliceOption[] policeGear = new PoliceOption[]
        {
            new PoliceOption("Handschellen", true, "polizei_handcuffs", "police_handcuffs", "handcuffs"),
            new PoliceOption("Pfefferspray", true, "pepperspray", "pepper_spray", "pepper spray"),
            new PoliceOption("Schlagstock", true, "polizei_stick", "police_stick", "baton", "schlagstock", "stick"),
            new PoliceOption("Schlagstockhalter", true, "stick_case", "stickcase", "baton_case"),
            new PoliceOption("Funkgeraet", true, "polizei_radio", "police_radio", "police_walkie", "funkgeraet"),
            new PoliceOption("Taser", true, "taser", "taster"),
            new PoliceOption("Taser-Holster", true, "taser_case", "taster_case", "tasercase"),
            new PoliceOption("Pistole", true, "polizei_gun", "police_gun", "pistol", "pistole", "gun"),
            new PoliceOption("Pistolenholster", true, "gun_case", "guncase", "holster", "pistol_case")
        };
        // Nur Datenarchiv fuer spaetere Polizeibekleidung: keine Aktivierung oder Menuefunktion.
        private readonly PoliceOption[] policeClothes = new PoliceOption[]
        {
            new PoliceOption("Schutzweste", false, "bulletproof_vest", "bulletproofvest", "bulletproof vest"),
            new PoliceOption("Lederhandschuhe", false, "leather_gloves", "gloves_leather", "lether_gloves", "leather gloves"),
            new PoliceOption("Anorak", false, "anorak"),
            new PoliceOption("Cardigan", false, "cardigan", "cadigan"),
            new PoliceOption("Latexhandschuhe", false, "gloves_latex", "latex_gloves", "latex"),
            new PoliceOption("Jacke", false, "jacket"),
            new PoliceOption("Polizeimuetze", false, "police_hat", "polizei_hat", "policehat"),
            new PoliceOption("Regenmantel", false, "raincoat", "rain_coat"),
            new PoliceOption("Hose", false, "pants", "police_pants", "polizei_pants"),
            new PoliceOption("Kurze Polizeihose", false, "police_short", "police_shorts", "polizei_short"),
            new PoliceOption("Schuhe", false, "polizei_shoe", "police_shoe", "shoe", "shoes", "boots", "stiefel", "schuhe"),
            new PoliceOption("Polizeihelm", false, "police_helmet", "polizei_helmet", "police_helm", "polizei_helm", "helmet", "helm")
        };
        // Original-Kleidungsrenderer der Spielfiguren werden getrennt von der Bundle-Kopie
        // verwaltet. Nur bekannte Kleidungs-Renderer werden geaendert; Ausruestung bleibt unberuehrt.
        private sealed class PoliceNativeClothing
        {
            public Transform Owner = null!;
            public Renderer Renderer = null!;
            public PoliceOption Option = null!;
            public bool InitialEnabled;
        }
        private readonly Dictionary<Renderer, PoliceNativeClothing> policeNativeClothes =
            new Dictionary<Renderer, PoliceNativeClothing>();
        private readonly HashSet<string> policeUserChosenClothes = new HashSet<string>();
        private readonly HashSet<int> policeNativeDiagnostics = new HashSet<int>();
        private readonly Dictionary<Transform, GameObject> policeAutoInstances = new Dictionary<Transform, GameObject>();
        private float policeNextAutoScan;
        private float policeNextLoadAttempt;
        private string policeLiveStatus = "Automatik wartet auf die Spielwelt";
        private bool policeAutoEnabled = false;
        private readonly Dictionary<SkinnedMeshRenderer, bool> policeHiddenDefaultRenderers =
            new Dictionary<SkinnedMeshRenderer, bool>();
        private readonly HashSet<int> policeReplacementWarnings = new HashSet<int>();
        private readonly HashSet<int> policeReplacementApplied = new HashSet<int>();
        private readonly HashSet<string> policeMissingReported = new HashSet<string>();
        private readonly HashSet<string> policeMaterialWarnings = new HashSet<string>();
        // Funkgeraet fuer Einsatzleiter, Notfallsanitaeter und Notarzt.
        // Standardmaessig EIN; niemals zivile Kleidung oder Originalrenderer veraendern.
        private bool fireRadioEnabled = true;
        private bool rescueRadioEnabled = true;
        private readonly Dictionary<Transform, GameObject?> serviceRadioInstances = new Dictionary<Transform, GameObject?>();
        private readonly Dictionary<Transform, int> serviceRadioKinds = new Dictionary<Transform, int>();
        private readonly HashSet<int> serviceRadioReported = new HashSet<int>();
        private readonly HashSet<int> serviceRadioOutfitReported = new HashSet<int>();
        private readonly HashSet<int> serviceRadioSkippedReported = new HashSet<int>();



        private static string PoliceNormalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static bool PoliceOptionMatches(string rendererName, PoliceOption option)
        {
            string full = PoliceNormalize(rendererName);
            if (full.Contains("lod1") || full.Contains("lod2") || full.Contains("lod3") ||
                full.Contains("lod4")) return false;

            // Strikte Mesh-Zuordnung: "Handschuhe" enthaelt "Schuhe"!
            // Ein kurzer Alias wie "shoe" darf deshalb niemals andere Kleidung treffen.
            // Die Bundle-Meshes heissen polizei_<teil>_LOD0.
            if (option.Label == "Schuhe")
                return full.StartsWith("polizeishoe") || full.StartsWith("policeshoe") ||
                       full.StartsWith("polizeischuhe") || full.StartsWith("policeschuhe");
            if (option.Label == "Latexhandschuhe")
                return full.StartsWith("polizeihandschuhelatex") ||
                       full.StartsWith("polizeigloveslatex") || full.StartsWith("policegloveslatex");
            if (option.Label == "Lederhandschuhe")
                return full.StartsWith("polizeiglovesleather") || full.StartsWith("policeglovesleather");
            if (option.Label == "Jacke")
                return full.StartsWith("polizeijacket") || full.StartsWith("policejacket");
            if (option.Label == "Hose")
                return full.StartsWith("polizeipolicepants") || full.StartsWith("policepants") ||
                       full.StartsWith("polizeipants");
            if (option.Label == "Kurze Polizeihose")
                return full.StartsWith("polizeishort") || full.StartsWith("policeshort");

            // Case/Holster getrennt von den Waffen. Auch hier keine Teilwort-Kollision.
            if (option.Label == "Schlagstock")
                return full.StartsWith("polizeisticklod") || full.StartsWith("polizeistick02lod") ||
                       full.StartsWith("policesticklod");
            if (option.Label == "Taser")
                return full.StartsWith("polizeitaserlod") || full.StartsWith("policetaserlod");
            if (option.Label == "Pistole")
                return full.StartsWith("polizeigunlod") || full.StartsWith("policegunlod");

            foreach (string alias in option.Aliases)
                if (full.Contains(PoliceNormalize(alias))) return true;
            return false;
        }

        private static bool IsPoliceCharacter(Transform t)
        {
            string n = t.name.ToLowerInvariant();
            return n.Contains("polizist") || n.Contains("policeman") || n.Contains("police");
        }

        private static Transform? FindOriginalPoliceBone(Dictionary<string, Transform> bones, string boneName)
        {
            Transform? result;
            return bones.TryGetValue(boneName, out result) ? result : null;
        }

        // Einander ausschliessende Outfit-Gruppen: keine Jacke durch Weste,
        // kein Regenmantel durch Anorak und keine lange/kurze Hose zugleich.
        private static int PoliceClothingGroup(PoliceOption option)
        {
            switch (option.Label)
            {
                case "Schutzweste": case "Anorak": case "Cardigan":
                case "Jacke": case "Regenmantel": return 1;
                case "Lederhandschuhe": case "Latexhandschuhe": return 2;
                case "Hose": case "Kurze Polizeihose": return 3;
                case "Polizeimuetze": case "Polizeihelm": return 4;
                default: return 0;
            }
        }

        private static bool PoliceIsLOD0(string name)
        {
            string n = PoliceNormalize(name);
            return !(n.Contains("lod1") || n.Contains("lod2") ||
                     n.Contains("lod3") || n.Contains("lod4"));
        }

        // Erfasst ausschliesslich die Kleidung, die das Spiel schon mitbringt.
        // Bundle-Nachfahren werden explizit ausgeschlossen.
        private void PoliceCaptureNativeClothing(Transform target)
        {
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || policeNativeClothes.ContainsKey(renderer)) continue;
                if (renderer.GetComponentInParent<Transform>() == null) continue;
                if (renderer.transform.name == "FC_Polizei_Autoausruestung") continue;
                bool fromBundle = false;
                for (Transform? ancestor = renderer.transform; ancestor != null && ancestor != target; ancestor = ancestor.parent)
                    if (ancestor.name == "FC_Polizei_Autoausruestung" || ancestor.name == "FC_Handcuffs_Test")
                    { fromBundle = true; break; }
                if (fromBundle) continue;

                // Namen der direkten Eltern enthalten bei extrahierten SkinnedMeshes
                // oft die Outfit-Bezeichnung, waehrend das Mesh selbst nur LOD0 heisst.
                string names = renderer.name;
                Transform? parent = renderer.transform.parent;
                int depth = 0;
                while (parent != null && parent != target && depth++ < 2)
                {
                    names += " " + parent.name;
                    parent = parent.parent;
                }
                PoliceOption? match = null;
                foreach (PoliceOption option in policeClothes)
                    if (PoliceOptionMatches(names, option)) { match = option; break; }
                if (match == null) continue;
                policeNativeClothes.Add(renderer, new PoliceNativeClothing
                { Owner = target, Renderer = renderer, Option = match, InitialEnabled = renderer.enabled });
                // Der Originalzustand wird NICHT ueberschrieben. Der Menue-Status
                // wird unten direkt vom Renderer gelesen.

            }
            // Einmalige Laufzeitdiagnose: Original-Renderer und ihre Eltern ermitteln.
            // So sehen wir auch Outfit-Objekte, deren Namen keine bekannten Aliase enthalten.
            if (policeNativeDiagnostics.Add(target.GetInstanceID()))
            {
                ;
                // Erweitertes, rein lesendes Diagnoseprotokoll:
                // Das gefundene "Policeman" kann nur ein Controller/Marker sein.
                // Deshalb auch Eltern, Geschwister und den Skelett-Besitzer untersuchen.
                PoliceDiagnoseCharacterHierarchy(target);

                foreach (Renderer r in target.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    bool isBundle = false;
                    for (Transform? a = r.transform; a != null && a != target; a = a.parent)
                        if (a.name == "FC_Polizei_Autoausruestung" || a.name == "FC_Handcuffs_Test")
                        { isBundle = true; break; }
                    if (isBundle) continue;
                    string path = r.name;
                    Transform? aParent = r.transform.parent;
                    for (int i = 0; aParent != null && aParent != target && i < 3; ++i, aParent = aParent.parent)
                        path = aParent.name + "/" + path;
                    ;
                }
            }
        }

        // NUR DIAGNOSE: keine Renderer, Bones oder Ausruestung veraendern.
        private void PoliceDiagnoseCharacterHierarchy(Transform target)
        {
            try
            {
                Transform root = target.root;
                ;
                Transform? current = target;
                for (int level = 0; current != null && level < 8; level++, current = current.parent)
                {
                    ;
                }
                int shown = 0;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || shown >= 180) break;
                    bool bundle = false;
                    for (Transform? p = r.transform; p != null; p = p.parent)
                        if (p.name == "FC_Polizei_Autoausruestung" || p.name == "FC_Handcuffs_Test")
                        { bundle = true; break; }
                    if (bundle) continue;
                    string path = r.name;
                    for (Transform? p = r.transform.parent; p != null && p != root; p = p.parent)
                        path = p.name + "/" + path;
                    string mesh = r is SkinnedMeshRenderer smr && smr.sharedMesh != null
                        ? smr.sharedMesh.name : "-";
                    ;
                    shown++;
                }
                int componentsShown = 0;
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || componentsShown >= 45) continue;
                    string n = c.GetType().Name.ToLowerInvariant();
                    if (!(n.Contains("avatar") || n.Contains("cloth") ||
                          n.Contains("outfit") || n.Contains("dress") ||
                          n.Contains("character") || n.Contains("skin") ||
                          n.Contains("uniform"))) continue;
                    ;
                    componentsShown++;
                }
            }
            catch (Exception)
            {
                ;
            }
        }

        private bool PoliceGroupAlreadyEnabledByAnother(PoliceOption candidate)
        {
            int group = PoliceClothingGroup(candidate);
            if (group == 0) return false;
            foreach (PoliceOption other in policeClothes)
                if (!ReferenceEquals(other, candidate) && other.Enabled &&
                    PoliceClothingGroup(other) == group) return true;
            return false;
        }

        private void PoliceApplyNativeClothes()
        {
            var dead = new List<Renderer>();
            foreach (var pair in policeNativeClothes)
            {
                Renderer renderer = pair.Key;
                PoliceNativeClothing entry = pair.Value;
                if (renderer == null || entry.Owner == null) { dead.Add(renderer!); continue; }
                // Nur das Kleidungsmodell schalten, niemals den NPC oder seine Knochen.
                // Die LODs der Originalfigur bleiben vom Spiel verwaltet.
                // Originalkleidung erst nach ausdruecklicher Benutzerwahl veraendern.
                // Ohne Wahl behaelt das Spiel die volle Kontrolle ueber den Startzustand.
                if (policeUserChosenClothes.Contains(entry.Option.Label))
                    renderer.enabled = entry.Option.Enabled;
            }
            foreach (Renderer renderer in dead) policeNativeClothes.Remove(renderer);
        }

        private float policeWorldDiagnosticAt = -1f;
        private int policeWorldDiagnosticRuns;

        // Nur lesender Welt-Scan. Die Spielfigur kann als eigener Scene-Root
        // geladen werden und ist dann kein Kind des "Policeman"-Controllers.
        private void PoliceDiagnoseWorldMeshes()
        {
            if (policeAutoInstances.Count == 0 || policeWorldDiagnosticRuns >= 2) return;
            if (policeWorldDiagnosticAt < 0f)
                policeWorldDiagnosticAt = Time.unscaledTime + 12f;
            if (Time.unscaledTime < policeWorldDiagnosticAt) return;
            policeWorldDiagnosticAt = Time.unscaledTime + 20f;
            policeWorldDiagnosticRuns++;
            try
            {
                int found = 0;
                int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
                ;
                for (int si = 0; si < scenes; si++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        if (root == null) continue;
                        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            if (smr == null) continue;
                            bool bundle = false;
                            for (Transform? a = smr.transform; a != null; a = a.parent)
                                if (a.name == "FC_Polizei_Autoausruestung" || a.name == "FC_Handcuffs_Test")
                                { bundle = true; break; }
                            if (bundle) continue;
                            string mesh = smr.sharedMesh != null ? smr.sharedMesh.name : "-";
                            string combined = (smr.name + " " + mesh + " " + root.name).ToLowerInvariant();
                            bool named = combined.Contains("poliz") || combined.Contains("police") ||
                                         combined.Contains("jacket") || combined.Contains("cloth") ||
                                         combined.Contains("uniform") || combined.Contains("shirt");
                            float nearest = float.MaxValue;
                            foreach (Transform owner in policeAutoInstances.Keys)
                                if (owner != null)
                                    nearest = Mathf.Min(nearest, Vector3.Distance(owner.position, smr.transform.position));
                            if (!named && nearest > 7f) continue;
                            if (found++ >= 90) break;
                            string path = smr.name;
                            for (Transform? a = smr.transform.parent; a != null && a != root.transform; a = a.parent)
                                path = a.name + "/" + path;
                            ;
                        }
                        if (found >= 90) break;
                    }
                    if (found >= 90) break;
                }
                ;
            }
            catch (Exception) { ; }
        }

        // Die Outfit-Erkennung liest ausschliesslich den Zustand des Spiels.
        // Kein SetOutfit/ToggleAppearance: das Spiel kontrolliert den Wechsel selbst.
        private static bool ServiceIsWorkingOutfit(Transform actor)
        {
            int work = Animator.StringToHash("Work");
            int leader = Animator.StringToHash("WorkLeader");
            int scba = Animator.StringToHash("SCBA");
            foreach (MonoBehaviour component in actor.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                Type type = component.GetType();
                MethodInfo isOutfit = type.GetMethod("IsOutfit", BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);
                if (isOutfit == null || isOutfit.ReturnType != typeof(bool)) continue;
                try
                {
                    if ((bool)isOutfit.Invoke(component, new object[] { work }) ||
                        (bool)isOutfit.Invoke(component, new object[] { leader }) ||
                        (bool)isOutfit.Invoke(component, new object[] { scba })) return true;
                }
                catch { }
            }
            return false; // Kein eindeutiger Outfit-Nachweis = kein Funkgeraet.
        }

        private static int ServiceRadioKind(Transform actor)
        {
            // Auch Elternnamen beruecksichtigen: Der Avatar heisst im Spiel
            // haeufig nur "Fireman", waehrend die Rolle am Elternobjekt steht.
            for (Transform t = actor; t != null; t = t.parent)
            {
                string n = PoliceNormalize(t.name);
                if (n.Contains("einsatzleiter") || n.Contains("incidentcommander") ||
                    n.Contains("incidentcmdr") || n.Contains("elwcommander") ||
                    n.Contains("firemanelwcommander") ||
                    n.Contains("firechief") || n.Contains("fireleader") ||
                    n.Contains("zugfuehrer") || n.Contains("zugfuhrer") ||
                    n.Contains("groupcommander") || n.Contains("gruppenfuehrer") ||
                    n.Contains("gruppenfuhrer") || n.Contains("commander")) return 1;
                if (n.Contains("notfallsanitaeter") || n.Contains("notfallsanitater") ||
                    n.Contains("paramedic") || n.Contains("notarzt") ||
                    n.Contains("nefleader") || n.Contains("firemannefleader") ||
                    n.Contains("emergencyphysician") ||
                    n.Contains("emergencydoctor")) return 2;
            }
            return 0;
        }

        private void ServiceRadioScan()
        {
            if (policeEquipmentPrefab == null) return;
            var dead = new List<Transform>();
            foreach (var pair in serviceRadioInstances)
                if (pair.Key == null || pair.Value == null) dead.Add(pair.Key!);
            foreach (Transform actor in dead)
            { serviceRadioInstances.Remove(actor); serviceRadioKinds.Remove(actor); }

            // Additiv geladene Weltszenen mit durchsuchen.
            for (int si = 0; si < UnityEngine.SceneManagement.SceneManager.sceneCount; si++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null || !root.activeInHierarchy) continue;
                    foreach (Transform actor in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (actor == null || !actor.gameObject.activeInHierarchy) continue;
                        int kind = ServiceRadioKind(actor);
                        if (kind == 0 || serviceRadioInstances.ContainsKey(actor)) continue;
                        // Nur die tatsaechliche Figur mit ihrem Skelett ausruesten.
                        if (FindPoliceBone(actor, "fireman01_Hips") == null)
                        {
                            serviceRadioSkippedReported.Add(actor.GetInstanceID());
                            continue;
                        }
                        bool nested = false;
                        foreach (Transform known in serviceRadioInstances.Keys)
                            if (known != null && (actor.IsChildOf(known) || known.IsChildOf(actor)))
                            { nested = true; break; }
                        if (nested) continue;
                        if (PoliceAttachEquipment(actor, true))
                        {
                            serviceRadioKinds[actor] = kind;
                            serviceRadioInstances[actor] = serviceRadioPendingInstance;
                            serviceRadioPendingInstance = null;
                            ;
                        }
                    }
                }
            }
            ServiceApplyRadio();
        }

        // Performance: Vollstaendiger Dienst-Funk-Scan nur bei geaenderter Szenenliste
        // oder per Menueknopf. Outfit und Live-Schalter bleiben separat aktuell.
        private string serviceRadioSceneSignature = "";
        private float serviceRadioNextOutfitCheck;
        private void ServiceRadioScanWhenScenesChange()
        {
            if (policeEquipmentPrefab == null) return;
            var signature = new System.Text.StringBuilder();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded) signature.Append(scene.handle).Append(':');
            }
            string current = signature.ToString();
            if (current == serviceRadioSceneSignature) return;
            serviceRadioSceneSignature = current;
            ServiceRadioScan();
        }

        private GameObject? serviceRadioPendingInstance;
        private void ServiceApplyRadio()
        {
            foreach (var pair in serviceRadioInstances)
            {
                Transform actor = pair.Key;
                GameObject? instance = pair.Value;
                if (actor == null || instance == null) continue;
                int kind;
                if (!serviceRadioKinds.TryGetValue(actor, out kind)) continue;
                bool workingOutfit = ServiceIsWorkingOutfit(actor);
                bool active = (kind == 1 ? fireRadioEnabled : rescueRadioEnabled) && workingOutfit;
                serviceRadioOutfitReported.Add(actor.GetInstanceID());
                foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;
                    bool radio = PoliceOptionMatches(renderer.name, policeGear[4]);
                    renderer.enabled = active && radio && PoliceIsLOD0(renderer.name);
                    if (renderer.enabled)
                    {
                        Transform ancestor = renderer.transform;
                        while (ancestor != null && ancestor != instance.transform.parent)
                        { ancestor.gameObject.SetActive(true); ancestor = ancestor.parent; }
                    }
                }
            }
        }

        private void PoliceAutoScan(bool force = false)
        {
            if (!policeAutoEnabled) return;
            if (!force && Time.unscaledTime < policeNextAutoScan) return;
            policeNextAutoScan = Time.unscaledTime + 3f;
            try
            {
                if (policeEquipmentPrefab == null)
                {
                    if (Time.unscaledTime >= policeNextLoadAttempt)
                    {
                        policeNextLoadAttempt = Time.unscaledTime + 10f;
                        if (File.Exists(PoliceBundlePath)) LoadPoliceBundle();
                    }
                    if (policeEquipmentPrefab == null) return;
                }
                var dead = new List<Transform>();
                foreach (var pair in policeAutoInstances)
                    if (pair.Key == null || pair.Value == null) dead.Add(pair.Key!);
                foreach (Transform key in dead) policeAutoInstances.Remove(key);

                int added = 0;
                foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (root == null || !root.activeInHierarchy) continue;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || !t.gameObject.activeInHierarchy || !IsPoliceCharacter(t)) continue;
                        if (FindPoliceBone(t, "fireman01_Hips") == null) continue;
                        if (policeAutoInstances.ContainsKey(t)) continue;
                        // Namen innerhalb einer bereits ausgeruesteten Figuren-Hierarchie ignorieren.
                        bool childOfKnownPolice = false;
                        foreach (Transform known in policeAutoInstances.Keys)
                            if (known != null && t.IsChildOf(known)) { childOfKnownPolice = true; break; }
                        if (childOfKnownPolice) continue;
                        if (PoliceAttachEquipment(t)) added++;
                    }
                }
                
                // Alte Bundle-Funkgeräte vorläufig deaktiviert.
                // Originalkleidung und Jacke bleiben vollstaendig unter Spielkontrolle.
            }
            catch (Exception) { ; }
        }

        private bool PoliceAttachEquipment(Transform target, bool serviceRadioOnly = false)
        {
            if (policeEquipmentPrefab == null || target == null) return false;
            GameObject? instance = null;
            try
            {
                // Die Bone-Tabelle VOR dem Klonen erzeugen: sonst kann FindPoliceBone
                // versehentlich Bones der geklonten Ausruestung statt des NPC finden.
                var targetBones = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                foreach (Transform bone in target.GetComponentsInChildren<Transform>(true))
                    if (!targetBones.ContainsKey(bone.name)) targetBones.Add(bone.name, bone);
                // PoliceCaptureNativeClothing(target); // deaktiviert: Originalbekleidung unberuehrt
                instance = Instantiate(policeEquipmentPrefab, target);
                instance.name = serviceRadioOnly ? "FC_Dienst_Funkgeraet" : "FC_Polizei_Autoausruestung";
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;
                // Das Bundle wird bewusst nur mit LOD0 dargestellt. Eigene
                // LODGroups duerfen bei Kamera-Zoom nicht auf die deaktivierten
                // LOD1-3 umschalten oder LOD0 ausblenden. Nur Bundle-Gruppen
                // deaktivieren, niemals die LODGroups des Original-Avatars.
                int disabledBundleLods = 0;
                foreach (LODGroup group in instance.GetComponentsInChildren<LODGroup>(true))
                {
                    if (group == null) continue;
                    group.enabled = false;
                    disabledBundleLods++;
                }
                foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh == null) continue;
                    Transform[] originals = smr.bones;
                    if (originals == null || originals.Length == 0) continue;
                    Transform[] mapped = new Transform[originals.Length];
                    bool valid = true;
                    for (int i = 0; i < originals.Length; ++i)
                    {
                        Transform? b = originals[i] == null ? null : FindOriginalPoliceBone(targetBones, originals[i].name);
                        if (b == null) { valid = false; break; }
                        mapped[i] = b;
                    }
                    Transform? rootBone = smr.rootBone == null ? null : FindOriginalPoliceBone(targetBones, smr.rootBone.name);
                    if (!valid || rootBone == null) continue;
                    smr.bones = mapped;
                    smr.rootBone = rootBone;
                    smr.updateWhenOffscreen = true;
                }
                PoliceRestoreOriginalMaterials(instance);
                if (serviceRadioOnly)
                {
                    serviceRadioPendingInstance = instance;
                    return true;
                }
                policeAutoInstances.Add(target, instance);
                PoliceApplyLiveOutfits();
                policeLiveStatus = "Automatisch ausgeruestete Polizisten: " + policeAutoInstances.Count;
                return true;
            }
            catch (Exception)
            {
                if (instance != null) Destroy(instance);
                ;
                return false;
            }
        }

        // Wenn die extrahierten Materialien keine Texturen besitzen, passende Original-
        // Materialien des laufenden Spiels bevorzugen. Keine kuenstliche Farb-/Shader-Aenderung.
        private void PoliceRestoreOriginalMaterials(GameObject instance)
        {
            var originals = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            foreach (Material mat in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (mat == null || !mat.HasProperty("_MainTex") || mat.mainTexture == null) continue;
                string key = mat.name.Replace(" (Instance)", "").Trim();
                if (!originals.ContainsKey(key)) originals.Add(key, mat);
            }
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                Material[] mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; ++i)
                {
                    Material current = mats[i];
                    if (current == null || !current.HasProperty("_MainTex")) continue;
                    if (current.mainTexture != null) continue;
                    string key = current.name.Replace(" (Instance)", "").Trim();
                    Material original;
                    if (originals.TryGetValue(key, out original) && original != current)
                    {
                        mats[i] = original;
                        changed = true;
                        ;
                    }
                    else
                        policeMaterialWarnings.Add(key);
                }
                if (changed) renderer.sharedMaterials = mats;
            }
        }

        // Der sichtbare Avatar liegt im Spiel oft AUSSERHALB des Policeman-Controllers.
        // Deshalb anhand der echten SkinnedMesh-Renderer in den geladenen Szenen suchen.
        // Nur das eindeutig zugeordnete Default_merged-Mesh veraendern; Head/Rig bleiben.
        // Die originale Jacke liegt in Default_merged zusammen mit Koerperteilen.
        // NIEMALS den gesamten Renderer ausschalten: sonst verschwinden Haende/Koerper.
        // Ein gezielter Austausch ist erst moeglich, wenn die Jacke als eigener
        // Submesh-/Materialbereich identifiziert ist.
        private readonly HashSet<int> policeJacketMeshDiagnostics = new HashSet<int>();
        private void PoliceApplyDefaultModelReplacement()
        {
            // Sicherheit: Niemals den ganzen Default_merged-Renderer verstecken.
            foreach (var pair in policeHiddenDefaultRenderers)
                if (pair.Key != null) pair.Key.enabled = pair.Value;
            policeHiddenDefaultRenderers.Clear();

            // Der sichtbare Avatar kann in einer anderen Szene/Hierarchie liegen
            // als das Objekt, an dem die Bundle-Ausruestung befestigt wurde.
            // Deshalb direkt die echte Policeman-Weltfigur untersuchen.
            if (policeAutoInstances.Count == 0) return;
            int found = 0;
            for (int si = 0; si < UnityEngine.SceneManagement.SceneManager.sceneCount; si++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null || root.name != "Policeman") continue;
                    foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr == null || smr.sharedMesh == null) continue;
                        if (PoliceNormalize(smr.name) != "defaultlod0" ||
                            PoliceNormalize(smr.sharedMesh.name) != "defaultmergedlod0") continue;
                        found++;
                        if (!policeJacketMeshDiagnostics.Add(smr.GetInstanceID())) continue;
                        Mesh mesh = smr.sharedMesh;
                        Material[] mats = smr.sharedMaterials;
                        ;
                        for (int mi = 0; mi < mats.Length; mi++)
                            ;
                        for (int sub = 0; sub < mesh.subMeshCount; sub++)
                        {
                            try
                            {
                                ;
                            }
                            catch (Exception)
                            {
                                ;
                            }
                        }
                    }
                }
            }
            if (found == 0) policeJacketMeshDiagnostics.Add(-1001);
        }


        // Nur Polizeiausruestung. Bekleidungsnamen bleiben im Code archiviert,
        // werden aber weder automatisch angelegt noch geschaltet.
        private void PoliceApplyLiveOutfits()
        {
            int active = 0;
            foreach (var pair in policeAutoInstances)
            {
                GameObject? instance = pair.Value;
                if (instance == null) continue;
                foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;
                    bool wanted = false;
                    foreach (PoliceOption option in policeGear)
                        if (option.Enabled && PoliceOptionMatches(renderer.name, option))
                        { wanted = true; break; }
                    bool hasMesh = !(renderer is SkinnedMeshRenderer) ||
                        ((SkinnedMeshRenderer)renderer).sharedMesh != null;
                    renderer.enabled = wanted && PoliceIsLOD0(renderer.name) && hasMesh;
                    if (renderer.enabled)
                    {
                        Transform? ancestor = renderer.transform;
                        while (ancestor != null && ancestor != instance.transform.parent)
                        { ancestor.gameObject.SetActive(true); ancestor = ancestor.parent; }
                        active++;
                    }
                }
            }
            policeLiveStatus = "Polizisten: " + policeAutoInstances.Count +
                " | aktive Ausruestungs-LOD0: " + active +
                " | Bekleidung: unveraendert (Spielsteuerung)";
        }

        private int PoliceCountActive(PoliceOption option, out int available)
        {
            int active = 0;
            available = 0;
            foreach (var pair in policeAutoInstances)
            {
                if (pair.Value == null) continue;
                foreach (Renderer renderer in pair.Value.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !PoliceOptionMatches(renderer.name, option)) continue;
                    available++;
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) active++;
                }
            }
            foreach (var pair in policeNativeClothes)
            {
                PoliceNativeClothing n = pair.Value;
                if (n.Renderer == null || n.Owner == null || !ReferenceEquals(n.Option, option)) continue;
                available++;
                if (n.Renderer.enabled && n.Renderer.gameObject.activeInHierarchy) active++;
            }
            return active;
        }

        private void PoliceDrawOptions(PoliceOption[] options)
        {
            foreach (PoliceOption option in options)
            {
                int available;
                int active = PoliceCountActive(option, out available);
                GUILayout.BeginHorizontal();
                bool isClothing = Array.IndexOf(policeClothes, option) >= 0;
                // Bei Originalkleidung kommt der Schalterstatus von der realen Figur.
                // Nur eine bewusste Benutzerwahl wird als Vorgabe gespeichert.
                bool shown = isClothing && !policeUserChosenClothes.Contains(option.Label)
                    ? active > 0 : option.Enabled;
                bool next = GUILayout.Toggle(shown, " " + option.Label, textStyle, GUILayout.MinWidth(290f));
                if (next != shown)
                {
                    option.Enabled = next;
                    if (Array.IndexOf(policeClothes, option) >= 0)
                    {
                        policeUserChosenClothes.Add(option.Label);
                        if (next)
                        {
                            int group = PoliceClothingGroup(option);
                            if (group != 0)
                                foreach (PoliceOption other in policeClothes)
                                    if (!ReferenceEquals(other, option) && PoliceClothingGroup(other) == group)
                                    { other.Enabled = false; policeUserChosenClothes.Add(other.Label); }
                        }
                    }
                    PoliceApplyLiveOutfits(); // Live und ohne uebereinanderliegende Kleidung.
                    active = PoliceCountActive(option, out available);
                }
                string state = available == 0 ? "NICHT GEFUNDEN" :
                    active > 0 ? "AKTIV (" + active + ")" : "INAKTIV";
                GUILayout.Label(state, infoStyle, GUILayout.Width(170f));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawPoliceBundleTab()
        {
            GUILayout.Label("POLIZEIAUSRUESTUNG", headingStyle);
            GUILayout.Label("Automatische Polizei-Ausruestung aus firecontrol_assets: " + fcGearStatus, infoStyle);
        }


        // Polizei-Ausruestung aus demselben firecontrol_assets wie POL_FC.
        // Keine globalen Material-Scans, kein Shader-Austausch, keine Asset-Duplikate.
        private GameObject? fcGearPrefab;
        private float fcGearNextScan;
        private readonly Dictionary<Transform, GameObject> fcGearInstances = new Dictionary<Transform, GameObject>();
        private readonly Dictionary<Renderer, bool> fcGearHiddenOriginals = new Dictionary<Renderer, bool>();
        private string fcGearStatus = "Warte auf Polizeifigur";

        private void FCLoadGearPrefab()
        {
            if (fcGearPrefab != null) return;
            // FCLoadVehicle verwaltet das gemeinsame Bundle. Nie zweimal LoadFromFile aufrufen.
            if (fcVehicleBundle == null) FCLoadVehicle();
            if (fcVehicleBundle == null) return;
            foreach (string name in fcVehicleBundle.GetAllAssetNames())
            {
                if (!name.EndsWith("/polizei_ausruestung_fc.prefab", StringComparison.OrdinalIgnoreCase)) continue;
                fcGearPrefab = fcVehicleBundle.LoadAsset<GameObject>(name);
                break;
            }
            fcGearStatus = fcGearPrefab == null ? "Polizei_Ausruestung_FC fehlt im Bundle" : "Polizeiausruestung geladen";
        }

        private static bool FCIsGearRenderer(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("polizei_") &&
                (n.Contains("handcuff") || n.Contains("pepperspray") || n.Contains("stick") ||
                 n.Contains("radio") || n.Contains("taser") || n.Contains("gun") ||
                 n.Contains("camera") || n.Contains("gloves_leather"));
        }

        private bool FCEquipPolice(Transform avatar)
        {
            if (fcGearPrefab == null || avatar == null || fcGearInstances.ContainsKey(avatar)) return false;
            var boneMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            foreach (Transform t in avatar.GetComponentsInChildren<Transform>(true))
            {
                // Das echte Avatar-Skelett bevorzugen, niemals Bones aus einer Bundle-Instanz.
                if (!boneMap.ContainsKey(t.name)) boneMap.Add(t.name, t);
            }
            if (!boneMap.ContainsKey("fireman01_Hips")) return false;
            GameObject? instance = null;
            try
            {
                instance = Instantiate(fcGearPrefab, avatar, false);
                instance.name = "FC_Polizei_Ausruestung_Runtime";
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;
                int ready = 0;
                var successfulNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || !FCIsGearRenderer(smr.name) || smr.sharedMesh == null) continue;
                    Transform[] original = smr.bones;
                    if (original == null || original.Length == 0 || smr.rootBone == null) { smr.enabled = false; continue; }
                    Transform[] mapped = new Transform[original.Length];
                    bool valid = true;
                    for (int i = 0; i < original.Length; i++)
                    {
                        if (original[i] == null || !boneMap.TryGetValue(original[i].name, out mapped[i]))
                        { valid = false; break; }
                    }
                    Transform mappedRoot;
                    if (!valid || !boneMap.TryGetValue(smr.rootBone.name, out mappedRoot))
                    { smr.enabled = false; continue; }
                    smr.bones = mapped;
                    smr.rootBone = mappedRoot;
                    // LODGroup steuert den sichtbaren Detailgrad. Kein manuelles Einschalten aller LODs.
                    smr.updateWhenOffscreen = true;
                    ready++;
                    string baseName = smr.name.ToLowerInvariant();
                    for (int lod = 0; lod < 4; lod++) baseName = baseName.Replace("_lod" + lod, "");
                    successfulNames.Add(baseName);
                }
                if (ready == 0) { Destroy(instance); fcGearStatus = "Keine gueltigen SkinnedMeshes/Bones"; return false; }
                // Bereits vorhandene gleichnamige Ausruestung ausblenden, damit keine Duplikate entstehen.
                // Nur Renderer innerhalb dieses Avatars, niemals globale Renderer oder Materialien veraendern.
                foreach (SkinnedMeshRenderer original in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (original == null || original.transform.IsChildOf(instance.transform) || !FCIsGearRenderer(original.name)) continue;
                    string baseName = original.name.ToLowerInvariant();
                    for (int lod = 0; lod < 4; lod++) baseName = baseName.Replace("_lod" + lod, "");
                    if (!successfulNames.Contains(baseName)) continue;
                    if (!fcGearHiddenOriginals.ContainsKey(original)) fcGearHiddenOriginals.Add(original, original.enabled);
                    original.enabled = false;
                }
                fcGearInstances.Add(avatar, instance);
                fcGearStatus = "Ausgeruestet: " + fcGearInstances.Count + " | Meshes: " + ready;
                return true;
            }
            catch (Exception ex)
            {
                if (instance != null) Destroy(instance);
                fcGearStatus = "Ausrüstung fehlgeschlagen: " + ex.Message;
                return false;
            }
        }

        private void FCUpdatePoliceGear()
        {
            if (Time.unscaledTime < fcGearNextScan) return;
            fcGearNextScan = Time.unscaledTime + 4f;
            FCLoadGearPrefab();
            if (fcGearPrefab == null) return;
            var gone = new List<Transform>();
            foreach (var pair in fcGearInstances)
                if (pair.Key == null || pair.Value == null) gone.Add(pair.Key!);
            foreach (Transform t in gone) fcGearInstances.Remove(t);
            // Nur aktive Szenenfiguren untersuchen. Niemals Resources.FindObjectsOfTypeAll<Material>().
            foreach (SkinnedMeshRenderer renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            {
                if (renderer == null || renderer.rootBone == null || !renderer.gameObject.scene.IsValid()) continue;
                Transform candidate = renderer.transform;
                while (candidate != null)
                {
                    if (candidate.name == "FC_Polizei_Ausruestung_Runtime") break;
                    if (candidate.name.IndexOf("Polizist_Male_01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        candidate.name.Equals("Policeman", StringComparison.OrdinalIgnoreCase))
                    {
                        if (candidate.gameObject.activeInHierarchy && !fcGearInstances.ContainsKey(candidate))
                            FCEquipPolice(candidate);
                        break;
                    }
                    candidate = candidate.parent;
                }
            }
        }

        private void FCCleanupPoliceGear()
        {
            foreach (var pair in fcGearInstances)
                if (pair.Value != null) Destroy(pair.Value);
            fcGearInstances.Clear();
            foreach (var pair in fcGearHiddenOriginals)
                if (pair.Key != null) pair.Key.enabled = pair.Value;
            fcGearHiddenOriginals.Clear();
            fcGearPrefab = null;
        }

        // FIRE CONTROL Fahrzeuge: separates Bundle, vorhandenes Polizei-Bundle bleibt unberuehrt.
        private AssetBundle? fcVehicleBundle;
        private GameObject? fcVehiclePrefab;
        private string fcVehicleStatus = "Noch nicht geladen";
        private string FCVehicleBundlePath => Path.Combine(Paths.PluginPath, "FireControl", "firecontrol_assets");

        private void FCLoadVehicle()
        {
            try
            {
                if (fcVehiclePrefab != null)
                {
                    fcVehicleStatus = "POL_FC bereits geladen";
                    return;
                }
                if (!File.Exists(FCVehicleBundlePath))
                {
                    fcVehicleStatus = "Bundle fehlt: " + FCVehicleBundlePath;
                    ;
                    return;
                }
                fcVehicleBundle = AssetBundle.LoadFromFile(FCVehicleBundlePath);
                if (fcVehicleBundle == null)
                {
                    fcVehicleStatus = "AssetBundle.LoadFromFile fehlgeschlagen";
                    ;
                    return;
                }
                foreach (string assetName in fcVehicleBundle.GetAllAssetNames())
                {
                    if (!assetName.EndsWith("/pol_fc.prefab", StringComparison.OrdinalIgnoreCase) &&
                        !assetName.EndsWith("pol_fc.prefab", StringComparison.OrdinalIgnoreCase))
                        continue;
                    fcVehiclePrefab = fcVehicleBundle.LoadAsset<GameObject>(assetName);
                    if (fcVehiclePrefab != null) break;
                }
                if (fcVehiclePrefab != null) FCDiagnoseNativePrefabLights();
                fcVehicleStatus = fcVehiclePrefab != null
                    ? "POL_FC geladen (noch nicht gespawnt)"
                    : "POL_FC.prefab im Bundle nicht gefunden";
                
            }
            catch (Exception ex)
            {
                fcVehicleStatus = "Ladefehler: " + ex.Message;
                ;
            }
        }

        // Uebertraegt die komplette sichtbare Prefab-Hierarchie. Bestehende Fahrzeug-
        // Transforms und Scripts bleiben erhalten (Tueranimationen, Netcode, Fahrphysik).
        // Nur Meshes, Renderer, Materialien und neue rein visuelle Kinder werden kopiert.
    }
}
