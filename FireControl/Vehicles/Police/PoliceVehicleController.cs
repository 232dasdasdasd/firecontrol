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

        private sealed class FCVisualBackup
        {
            public MeshFilter Filter = null!;
            public Mesh? Mesh;
            public MeshRenderer? Renderer;
            public Material[]? Materials;
            public bool RendererEnabled;
        }
        private readonly List<FCVisualBackup> fcVisualBackups = new List<FCVisualBackup>();

        private static void FCCopyVisualTree(Transform source, Transform destination)
        {
            GameObject child = new GameObject(source.name);
            child.layer = destination.gameObject.layer;
            child.transform.SetParent(destination, false);
            child.transform.localPosition = source.localPosition;
            child.transform.localRotation = source.localRotation;
            child.transform.localScale = source.localScale;
            MeshFilter filter = source.GetComponent<MeshFilter>();
            MeshRenderer renderer = source.GetComponent<MeshRenderer>();
            if (filter != null && renderer != null && filter.sharedMesh != null)
            {
                child.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer copied = child.AddComponent<MeshRenderer>();
                copied.sharedMaterials = renderer.sharedMaterials;
                copied.enabled = renderer.enabled;
                copied.shadowCastingMode = renderer.shadowCastingMode;
                copied.receiveShadows = renderer.receiveShadows;
            }
            foreach (Transform nested in source) FCCopyVisualTree(nested, child.transform);
            child.SetActive(source.gameObject.activeSelf);
        }

        private void FCMergeVisualTree(Transform source, Transform target, FCRealPoliceFlash entry)
        {
            // Gleiche Namen: Original-Transform behalten, damit Animation/LOD/Interaktion funktionieren.
            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceFilter != null && sourceRenderer != null && sourceFilter.sharedMesh != null)
            {
                MeshFilter targetFilter = target.GetComponent<MeshFilter>();
                MeshRenderer targetRenderer = target.GetComponent<MeshRenderer>();
                if (targetFilter != null && targetRenderer != null)
                {
                    // Originale Fahrzeugmeshes und -materialien niemals ersetzen.
                    // Das Notruf-112-Skinsystem verwaltet diese Renderer selbst.
                    // FireControl ergaenzt weiterhin fehlende Visuals und Lichtobjekte.
                }
                else if (targetFilter == null && targetRenderer == null)
                {
                    // Ein bisher leerer Anker darf neue visuelle Komponenten erhalten.
                    target.gameObject.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
                    MeshRenderer added = target.gameObject.AddComponent<MeshRenderer>();
                    added.sharedMaterials = sourceRenderer.sharedMaterials;
                    added.enabled = sourceRenderer.enabled;
                    entry.AddedComponents.Add(added);
                    entry.AddedComponents.Add(target.GetComponent<MeshFilter>());
                }
            }
            foreach (Transform sourceChild in source)
            {
                // Bestehende Tueren/LOD behalten. Nur einmal unter den Kindern suchen.
                Transform? targetChild = null;
                string wantedName = sourceChild.name.Trim();
                foreach (Transform child in target)
                    if (string.Equals(child.name.Trim(), wantedName, StringComparison.OrdinalIgnoreCase))
                    { targetChild = child; break; }
                if (targetChild != null)
                    FCMergeVisualTree(sourceChild, targetChild, entry);
                else
                {
                    FCCopyVisualTree(sourceChild, target);
                    Transform added = target.Find(sourceChild.name);
                    if (added != null) entry.Addons.Add(added.gameObject);
                    
                }
            }
        }

        // POL_FC-Frontblitzer direkt am echten, vom Spiel erzeugten Polizeifahrzeug.
        // Es wird KEIN zweites Car/NetworkObject erzeugt und die Fahrphysik bleibt original.
        private sealed class FCRealPoliceFlash
        {
            public Transform Root = null!;
            public Component Controller = null!;
            public int BlueMask;
            public Transform? MatrixRoot;
            public bool? MatrixLastEnabled;
            public Component? MatrixController;
            public Transform? MatrixPanel;
            public readonly List<Material> Materials = new List<Material>();
            public readonly Dictionary<Material, Color> OriginalEmissions = new Dictionary<Material, Color>();
            public readonly List<Light> Lights = new List<Light>();
            public readonly List<GameObject> Addons = new List<GameObject>();
            public readonly List<Component> AddedComponents = new List<Component>();
            public readonly Dictionary<string, Array> NativeOriginalGroups = new Dictionary<string, Array>();
            public readonly List<GameObject> NativeObjects = new List<GameObject>();
        }
        // MATRIX-EINSTELLUNGEN (Standard: erster Clip, Index 0).
        // Bevorzugt den Clipnamen, falls gesetzt; andernfalls wird der Index verwendet.
        private const string FCMatrixAnimationName = "";
        private const int FCMatrixAnimationIndex = 0;
        private static readonly Dictionary<Component, Component> fcMatrixButtons = new Dictionary<Component, Component>();
        private static readonly Dictionary<Component, FCRealPoliceFlash> fcMatrixButtonEntries = new Dictionary<Component, FCRealPoliceFlash>();
        private static Harmony? fcMatrixHarmony;

        // Direkte Unity-Legacy-Animation: der RTW besitzt die Clips auf der
        // Animation-Komponente, aber die exportierte matrixAnimations-Liste ist leer.
        // Die Spielmethode UpdateRemote wird deshalb fuer POL_FC NICHT aufgerufen.
        private static bool FCPlayPoliceMatrix(FCRealPoliceFlash entry, bool enabled)
        {
            if (entry.MatrixRoot == null || entry.MatrixController == null) return false;
            Animation animation = entry.MatrixController.GetComponent<Animation>();
            if (animation == null)
            {
                UnityEngine.Debug.LogWarning("[FireControl MATRIX] Unity Animation am Quad fehlt.");
                return false;
            }
            string clipName = enabled ? "RTW_matrix_Einsatz" : "RTW_matrix_empty";
            AnimationClip clip = animation.GetClip(clipName);
            if (clip == null)
            {
                UnityEngine.Debug.LogWarning("[FireControl MATRIX] Clip nicht gefunden: " + clipName);
                return false;
            }
            animation.enabled = true;
            animation.Stop();
            // Nur den Zustand dieser Fahrzeuginstanz konfigurieren, nicht den
            // gemeinsam verwendeten AnimationClip anderer Fahrzeuge veraendern.
            AnimationState state = animation[clipName];
            if (state != null) state.wrapMode = WrapMode.Loop;
            if (!animation.Play(clipName))
            {
                UnityEngine.Debug.LogWarning("[FireControl MATRIX] Animation.Play fehlgeschlagen: " + clipName);
                return false;
            }
            FieldInfo maskField = FCNativeField(entry.MatrixController.GetType(), "functionMask");
            if (maskField != null)
            {
                int mask = enabled ? 1 : 0;
                maskField.SetValue(entry.MatrixController, mask);
            }
            // Der native Changed-Event informiert ggf. weitere Spiel-Panels.
            FieldInfo changedField = FCNativeField(entry.MatrixController.GetType(), "changed");
            Delegate changed = changedField?.GetValue(entry.MatrixController) as Delegate;
            if (changed != null)
            {
                try { changed.DynamicInvoke(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("[FireControl MATRIX] Changed-Event: " + ex.Message); }
            }
            entry.MatrixLastEnabled = enabled;
            FCRefreshMatrixPanel(entry);
            FCUpdateMatrixButtonSprites(entry);
            UnityEngine.Debug.Log("[FireControl MATRIX] Direktanimation " + clipName + " | " + (enabled ? "EIN" : "AUS"));
            return true;
        }

        // Der exportierte MatrixButton hat ohne native matrixAnimations-Liste
        // animationIndex=-1. Daher meldet IsActive() immer false, obwohl die
        // Matrix ueber functionMask korrekt eingeschaltet wurde. Nur das eigene
        // POL_FC-Matrixpanel bekommt den passenden visuellen Zustand.
        // Der originale PanelElement.UpdateState kann den Sprite nach unserem
        // Tastendruck wieder ueberschreiben. Der Postfix korrigiert ausschliesslich
        // die registrierten POL_FC-Matrixbuttons nach nativen Panel-Updates.
        private static void FCMatrixStatePostfix(object __instance)
        {
            Component button = __instance as Component;
            if (button == null || !fcMatrixButtonEntries.TryGetValue(button, out FCRealPoliceFlash entry)) return;
            FCApplyMatrixButtonSprite(button, entry);
        }

        // Die originale PanelElement-Klasse besitzt zwei verschiedene Anzeigeziele:
        // target (SpriteRenderer) und targetImage (UnityEngine.UI.Image).
        // UnityEngine.UI ist in diesem Projekt nicht referenziert, daher wird
        // targetImage ausschliesslich ueber Reflection angesprochen.
        private static readonly HashSet<int> fcMatrixSpriteWarnings = new HashSet<int>();

        private static void FCApplyMatrixButtonSprite(Component button, FCRealPoliceFlash entry)
        {
            try
            {
                bool active = entry.MatrixLastEnabled ?? false;
                FCNativeField(button.GetType(), "animationIndex")?.SetValue(button, 0);
                string spriteField = active ? "activeState" : "onState";
                string wantedSprite = active ? "kef_matrix_a" : "kef_matrix";
                FieldInfo spriteReference = FCNativeField(button.GetType(), spriteField);
                Sprite sprite = spriteReference?.GetValue(button) as Sprite;

                // Die exportierte onState-Referenz ist nicht zwingend das
                // Standbild kef_matrix. Beide Zustaende explizit nach Namen
                // aufloesen und am geklonten RTW-Button zwischenspeichern.
                if (sprite == null || !string.Equals(sprite.name, wantedSprite, StringComparison.OrdinalIgnoreCase))
                {
                    Sprite matching = null;
                    foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
                        if (candidate != null && string.Equals(candidate.name, wantedSprite, StringComparison.OrdinalIgnoreCase))
                        { matching = candidate; break; }
                    if (matching != null)
                    {
                        sprite = matching;
                        spriteReference?.SetValue(button, matching);
                    }
                    else if (fcMatrixSpriteWarnings.Add(button.GetInstanceID() * 2 + (active ? 1 : 0)))
                        UnityEngine.Debug.LogWarning("[FireControl MATRIX] Stand-/Aktiv-Sprite nicht gefunden: " + wantedSprite +
                            " | Button=" + button.name + " | Fallback=" + (sprite == null ? "FEHLT" : sprite.name));
                }

                SpriteRenderer renderer = FCNativeField(button.GetType(), "target")?.GetValue(button) as SpriteRenderer;
                if (renderer == null) renderer = button.GetComponent<SpriteRenderer>();
                if (renderer == null) renderer = button.GetComponentInChildren<SpriteRenderer>(true);

                // PanelElement.targetImage ist ein UnityEngine.UI.Image, das wir
                // ohne zusaetzlichen Assemblyverweis ueber die Sprite-Property setzen.
                object targetImage = FCNativeField(button.GetType(), "targetImage")?.GetValue(button);
                bool imageChanged = false;
                if (targetImage is UnityEngine.Object unityImage && unityImage != null && sprite != null)
                {
                    PropertyInfo imageSprite = targetImage.GetType().GetProperty("sprite", BindingFlags.Public | BindingFlags.Instance);
                    if (imageSprite != null && imageSprite.CanWrite)
                    {
                        imageSprite.SetValue(targetImage, sprite, null);
                        imageChanged = true;
                    }
                }
                if (renderer != null && sprite != null)
                {
                    renderer.sprite = sprite;
                    renderer.enabled = true;
                }

                FieldInfo stateField = FCNativeField(button.GetType(), "elementState");
                if (stateField != null) stateField.SetValue(button, Enum.ToObject(stateField.FieldType, active ? 2 : 1));

                if ((sprite == null || (renderer == null && !imageChanged)) && fcMatrixSpriteWarnings.Add(button.GetInstanceID()))
                    UnityEngine.Debug.LogWarning("[FireControl MATRIX] Button-Diagnose " + button.name +
                        " | Status=" + (active ? "EIN" : "AUS") +
                        " | Sprite=" + (sprite == null ? "FEHLT" : sprite.name) +
                        " | target=" + (renderer == null ? "FEHLT" : renderer.name) +
                        " | targetImage=" + (targetImage == null ? "FEHLT" : targetImage.GetType().FullName));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[FireControl MATRIX] Button-Sprite-Update: " + ex.Message);
            }
        }

        private static void FCUpdateMatrixButtonSprites(FCRealPoliceFlash entry)
        {
            if (entry.MatrixPanel == null) return;
            Type buttonType = AccessTools.TypeByName("LocalPanel.MatrixButton");
            if (buttonType == null) return;
            foreach (Component button in entry.MatrixPanel.GetComponentsInChildren(buttonType, true))
                if (button != null && fcMatrixButtonEntries.ContainsKey(button)) FCApplyMatrixButtonSprite(button, entry);
        }

        private static bool FCMatrixButtonPrefix(object __instance)
        {
            Component button = __instance as Component;
            if (button == null || !fcMatrixButtonEntries.TryGetValue(button, out FCRealPoliceFlash entry))
                return true;
            try
            {
                bool next = !(entry.MatrixLastEnabled ?? false);
                FCPlayPoliceMatrix(entry, next);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[FireControl MATRIX] Direktsteuerung fehlgeschlagen: " + ex);
            }
            // Nur die von FireControl registrierte POL_FC-Taste abfangen.
            return false;
        }

        private static void FCEnsureMatrixClips(FCRealPoliceFlash entry, Transform sourceMatrix)
        {
            if (entry.MatrixController == null) return;
            Animation target = entry.MatrixController.GetComponent<Animation>();
            Animation source = sourceMatrix == null ? null : sourceMatrix.GetComponentInChildren<Animation>(true);
            if (target == null) return;
            foreach (string name in new[] { "RTW_matrix_empty", "RTW_matrix_Einsatz" })
            {
                if (target.GetClip(name) != null) continue;
                AnimationClip clip = source == null ? null : source.GetClip(name);
                if (clip == null)
                {
                    UnityEngine.Debug.LogWarning("[FireControl MATRIX] Clip fehlt auch im POL_FC-Prefab: " + name);
                    continue;
                }
                target.AddClip(clip, name);
            }
            UnityEngine.Debug.Log("[FireControl MATRIX] Legacy-Clips: empty=" + (target.GetClip("RTW_matrix_empty") != null) +
                " | Einsatz=" + (target.GetClip("RTW_matrix_Einsatz") != null));
        }

        private static void FCInstallMatrixButtonPatch()
        {
            if (fcMatrixHarmony != null) return;
            Type type = AccessTools.TypeByName("LocalPanel.MatrixButton");
            MethodInfo method = type == null ? null : AccessTools.Method(type, "OnBeginUse");
            if (method == null) return;
            var harmony = new Harmony("de.notruf112.firecontrol.matrixbutton");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModMenu), nameof(FCMatrixButtonPrefix)));
            // MatrixButton erbt UpdateState von PanelElement. Nur nach dessen
            // Ausfuehrung wird der POL_FC-Button optisch synchronisiert.
            Type elementType = AccessTools.TypeByName("LocalPanel.PanelElement");
            MethodInfo stateMethod = elementType == null ? null : AccessTools.Method(elementType, "UpdateState");
            if (stateMethod != null)
                harmony.Patch(stateMethod, postfix: new HarmonyMethod(typeof(ModMenu), nameof(FCMatrixStatePostfix)));
            fcMatrixHarmony = harmony;
        }

        private void FCSetupPoliceMatrix(FCRealPoliceFlash entry)
        {
            if (entry.MatrixRoot == null) return;
            Type matrixType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarMatrixController");
            Type panelType = AccessTools.TypeByName("LocalPanel.CarPanel");
            Type buttonType = AccessTools.TypeByName("LocalPanel.MatrixButton");
            Type panelControllerType = AccessTools.TypeByName("LocalPanel.CarPanelController");
            if (matrixType == null || panelType == null || buttonType == null || panelControllerType == null) return;
            Component matrix = entry.MatrixRoot.GetComponentInChildren(matrixType, true);
            if (matrix == null) return;
            entry.MatrixController = matrix;
            // Die exportierte native matrixAnimations-Liste ist leer. BuildMatrixSetup()
            // wuerde die zwei bereits vorhandenen Unity-Animation-Clips entfernen.
            // Stattdessen Clips erhalten bzw. vom POL_FC-Prefab nachtragen.
            FCEnsureMatrixClips(entry, fcVehiclePrefab == null ? null : fcVehiclePrefab.transform.Find("Matrix"));
            Transform sourcePanel = fcVehiclePrefab.transform.Find("VehicleCockpitPanel_RTW_Matrix");
            Transform panel = entry.Root.Find("VehicleCockpitPanel_RTW_Matrix");
            if (panel == null && sourcePanel != null)
            {
                GameObject clone = UnityEngine.Object.Instantiate(sourcePanel.gameObject, entry.Root, false);
                clone.name = sourcePanel.name;
                clone.SetActive(true);
                entry.Addons.Add(clone);
                panel = clone.transform;
            }
            if (panel == null) return;
            entry.MatrixPanel = panel;
            panel.gameObject.SetActive(true);
            Component mainController = entry.Root.GetComponent(panelControllerType);
            Component carPanel = panel.GetComponent(panelType);
            if (mainController == null || carPanel == null) return;
            // Das originale Setup() sucht die Matrix nicht selbst: explizit verknuepfen.
            FCNativeField(panelControllerType, "matrixController")?.SetValue(mainController, matrix);
            FieldInfo panelsField = FCNativeField(panelControllerType, "panels");
            Array originalPanels = panelsField?.GetValue(mainController) as Array;
            bool alreadyPresent = false;
            if (originalPanels != null)
                foreach (object item in originalPanels) if (ReferenceEquals(item, carPanel)) alreadyPresent = true;
            if (!alreadyPresent && panelsField != null)
            {
                int count = originalPanels?.Length ?? 0;
                Array newPanels = Array.CreateInstance(panelType, count + 1);
                if (originalPanels != null) Array.Copy(originalPanels, newPanels, count);
                newPanels.SetValue(carPanel, count);
                panelsField.SetValue(mainController, newPanels);
            }
            AccessTools.Method(panelType, "Setup")?.Invoke(carPanel, new object[] { mainController });
            // Bei Laufzeit-Anbau wurde Start() bereits ausgefuehrt: Panel selbst initialisieren.
            Type carType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.Car");
            Type soundType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarSoundController");
            Component car = carType == null ? null : entry.Root.GetComponent(carType);
            Component sound = soundType == null ? null : entry.Root.GetComponentInChildren(soundType, true);
            AccessTools.Method(panelType, "Initialize")?.Invoke(carPanel, new object[] { car, sound, entry.Controller, matrix });
            Component[] buttons = panel.GetComponentsInChildren(buttonType, true);
            FCInstallMatrixButtonPatch();
            foreach (Component button in buttons)
            {
                fcMatrixButtons[button] = matrix;
                fcMatrixButtonEntries[button] = entry;
                FCNativeField(button.GetType(), "animationIndex")?.SetValue(button, 0);
            }
            // Nachtraeglich eingefuegte Panels erhalten keinen regulaeren Start()-Durchlauf
            // des bereits laufenden CarPanelController. Den Matrixknopf deshalb sofort
            // in den bedienbaren Zustand versetzen und seine Interaktion freigeben.
            FCPlayPoliceMatrix(entry, false);
            // Das Panel wird in ein bereits gestartetes Fahrzeug eingesetzt.
            // Native Start-/Panel-Updates koennen die Interaktion im selben Frame
            // erneut sperren. Einmalig zeitversetzt nachinitialisieren.
            StartCoroutine(FCFinalizeMatrixPanel(entry));
        }

        private IEnumerator FCFinalizeMatrixPanel(FCRealPoliceFlash entry)
        {
            // Kein Update-Loop und keine globale Suche: nur dieses neue Panel.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (entry.MatrixPanel == null || entry.MatrixController == null) yield break;
            FCRefreshMatrixPanel(entry);
            yield return new WaitForSecondsRealtime(0.5f);
            if (entry.MatrixPanel == null || entry.MatrixController == null) yield break;
            FCRefreshMatrixPanel(entry);
            Type buttonType = AccessTools.TypeByName("LocalPanel.MatrixButton");
            if (buttonType == null) yield break;
            foreach (Component button in entry.MatrixPanel.GetComponentsInChildren(buttonType, true))
            {
                if (button == null) continue;
                Component interaction = button.GetComponent(AccessTools.TypeByName("LocalInteraction") ?? typeof(Transform));
                UnityEngine.Debug.Log("[FireControl MATRIX] Nachinitialisierung: Button=" + button.name +
                    " | aktiv=" + button.gameObject.activeInHierarchy +
                    " | Collider=" + (button.GetComponent<Collider>() != null) +
                    " | LocalInteraction=" + (interaction != null && !(interaction is Transform)) +
                    " | Patch=" + (fcMatrixHarmony != null));
            }
        }

        // Nur das zusaetzliche Matrixpanel aktualisieren. Die anderen Cockpitpanels
        // und ihre originalen Schaltbedingungen werden nicht veraendert.
        private static void FCRefreshMatrixPanel(FCRealPoliceFlash entry)
        {
            if (entry.MatrixPanel == null || entry.MatrixController == null) return;
            Type panelType = AccessTools.TypeByName("LocalPanel.CarPanel");
            Type buttonType = AccessTools.TypeByName("LocalPanel.MatrixButton");
            if (panelType == null || buttonType == null) return;
            Component panel = entry.MatrixPanel.GetComponent(panelType);
            if (panel == null) return;
            try
            {
                // Das Matrixpanel soll unabhaengig vom Motorstatus benutzbar sein.
                // Bei den originalen Panels bleibt die Motorabhaengigkeit bestehen.
                PropertyInfo maskProperty = AccessTools.Property(entry.MatrixController.GetType(), "Mask");
                FieldInfo maskField = FCNativeField(entry.MatrixController.GetType(), "Mask");
                object raw = maskProperty != null ? maskProperty.GetValue(entry.MatrixController, null)
                    : maskField?.GetValue(entry.MatrixController);
                int matrixMask = raw == null ? 0 : Convert.ToInt32(raw);
                MethodInfo update = AccessTools.Method(panelType, "UpdateState", new[] {
                    typeof(bool), typeof(int), typeof(int), typeof(int) });
                update?.Invoke(panel, new object[] { true, 0, 0, matrixMask });
                FCUpdateMatrixButtonSprites(entry);
                foreach (Component button in entry.MatrixPanel.GetComponentsInChildren(buttonType, true))
                {
                    if (button == null || !button.gameObject.activeInHierarchy) continue;
                    foreach (Collider collider in button.GetComponents<Collider>())
                        if (collider != null && !collider.enabled) collider.enabled = true;
                    // LocalInteraction wird vom Spiel in Awake registriert; beim
                    // spaeteren Einbau muss es weiterhin als interaktiv gelten.
                    Component interaction = button.GetComponent(
                        AccessTools.TypeByName("LocalInteraction") ?? typeof(Transform));
                    if (interaction != null && !(interaction is Transform))
                    {
                        PropertyInfo interactive = AccessTools.Property(interaction.GetType(), "IsInteractive");
                        bool active = interactive != null && Convert.ToBoolean(interactive.GetValue(interaction, null));
                        if (!active)
                            AccessTools.Method(interaction.GetType(), "SetInteractive", new[] { typeof(bool) })
                                ?.Invoke(interaction, new object[] { true });
                    }
                }
            }
            catch (Exception ex)
            {
                // Fehler nur im Unity/BepInEx-Log ausgeben; keine anderen Panels beeinflussen.
                UnityEngine.Debug.LogWarning("[FireControl] Matrixpanel-Interaktion: " + ex.Message);
            }
        }

        private readonly List<FCRealPoliceFlash> fcRealPolice = new List<FCRealPoliceFlash>();
        private float fcRealPoliceNextScan;
        private bool fcRealPoliceEnabled = true;

        // Spawn-Registrierung: kein globaler 6-Sekunden-Scan im laufenden Spiel.
        // Der Harmony-Hook merkt sich nur neue Controller; die Verarbeitung erfolgt
        // spaeter auf dem Unity-Hauptthread und wird pro Frame begrenzt.
        private static ModMenu? fcSpawnOwner;
        private static Harmony? fcSpawnHarmony;
        private readonly Queue<Component> fcPendingControllers = new Queue<Component>();
        private readonly HashSet<int> fcPendingIds = new HashSet<int>();
        private readonly HashSet<int> fcProcessedPoliceIds = new HashSet<int>();
        private bool fcInitialDiscoveryDone;
        private float fcNextPendingProcess;
        // Pro Controller nur einmal nach FC_AUTO-Markern suchen.
        private readonly Queue<Component> fcAutoPendingControllers = new Queue<Component>();
        private readonly HashSet<int> fcAutoSeenIds = new HashSet<int>();

        private static void FCNativeControllerStartPostfix(object __instance)
        {
            Component controller = __instance as Component;
            if (controller != null && fcSpawnOwner != null)
                fcSpawnOwner.FCQueueController(controller);
        }

        private void FCQueueController(Component controller)
        {
            if (controller == null) return;
            int id = controller.GetInstanceID();
            if (!fcProcessedPoliceIds.Contains(id) && fcPendingIds.Add(id))
            {
                fcPendingControllers.Enqueue(controller);
                FCQueueAutoLights(controller);
            }
        }

        private void FCQueueAutoLights(Component controller)
        {
            if (controller == null) return;
            int id = controller.GetInstanceID();
            if (fcAutoSeenIds.Add(id)) fcAutoPendingControllers.Enqueue(controller);
        }

        private void FCEnsureSpawnRegistration()
        {
            fcSpawnOwner = this;
            if (fcSpawnHarmony != null) return;
            try
            {
                Type type = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightController");
                MethodInfo start = type == null ? null : AccessTools.Method(type, "Start");
                if (start == null) return;
                var harmony = new Harmony("de.notruf112.firecontrol.police-spawn-optimized");
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(ModMenu), nameof(FCNativeControllerStartPostfix)));
                fcSpawnHarmony = harmony;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[FireControl] Spawn-Hook: " + ex.Message);
            }
        }

        private static Transform? FCFindCarRoot(Transform from)
        {
            for (Transform t = from; t != null; t = t.parent)
                foreach (Component c in t.GetComponents<Component>())
                    if (c != null && c.GetType().FullName == "com.crenetic.N2019Plugins.Vehicle.Car")
                        return t;
            return null;
        }

        private static bool FCIsPolice(Transform root)
        {
            // Kein Erkennen ueber allgemein vorhandene Cockpit-/Kofferraum-Teile:
            // diese waren zuvor auch bei RTW, TLF, LF24 usw. vorhanden.
            string name = root.name.ToLowerInvariant();
            if (name == "pol" || name.StartsWith("pol(") ||
                name.StartsWith("pol_") || name.Contains("police") ||
                name.Contains("polizei")) return true;
            Transform visuals = root.Find("Visuals");
            if (visuals == null) return false;
            foreach (Transform child in visuals)
                if (child.name.IndexOf("policecar", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Phase 1: native Prefab-Verknuepfungen auslesen, ohne sie zu veraendern.
        // Damit kann der naechste Schritt generisch nach Transform-Pfad statt
        // anhand fest codierter Blitzer-Namen verbinden.
        private void FCDiagnoseNativePrefabLights()
        {
            if (fcVehiclePrefab == null) return;
            try
            {
                Transform signals = fcVehiclePrefab.transform.Find("LightSignals");
                if (signals == null) { ; return; }
                var report = new StringBuilder();
                report.AppendLine("FIRE CONTROL - POL_FC native LightSignals Referenzen");
                report.AppendLine("Nur Diagnose; keine Material- oder Emissionsaenderungen.");
                foreach (Component component in signals.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    Type type = component.GetType();
                    if (type.FullName == null || !type.FullName.Contains("CarLight")) continue;
                    report.AppendLine("COMPONENT " + FCNativePath(component.transform, fcVehiclePrefab.transform) + " | " + type.FullName);
                    for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                    {
                        foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (field.IsStatic) continue;
                            bool serialized = field.IsPublic || Attribute.IsDefined(field, typeof(SerializeField));
                            if (!serialized) continue;
                            object value;
                            try { value = field.GetValue(component); } catch { continue; }
                            if (value is Component c)
                                report.AppendLine("  REF " + field.Name + " => " + FCNativePath(c.transform, fcVehiclePrefab.transform) + " [" + c.GetType().Name + "]");
                            else if (value is GameObject go)
                                report.AppendLine("  REF " + field.Name + " => " + FCNativePath(go.transform, fcVehiclePrefab.transform));
                            else if (value is UnityEngine.Object obj)
                                report.AppendLine("  REF " + field.Name + " => " + obj.name + " [" + obj.GetType().Name + "]");
                            else if (value is System.Collections.IEnumerable values && !(value is string))
                            {
                                int index = 0;
                                foreach (object item in values)
                                {
                                    if (index >= 64) break;
                                    if (item is Component ic) report.AppendLine("  REF " + field.Name + "[" + index + "] => " + FCNativePath(ic.transform, fcVehiclePrefab.transform));
                                    else if (item is GameObject ig) report.AppendLine("  REF " + field.Name + "[" + index + "] => " + FCNativePath(ig.transform, fcVehiclePrefab.transform));
                                    index++;
                                }
                            }
                        }
                    }
                }
                string dir = Path.Combine(Paths.ConfigPath, "FireControl");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "POL_FC_NativeLightSignals.txt");
                ;
                ;
            }
            catch (Exception) { ; }
        }

        private static string FCNativePath(Transform item, Transform root)
        {
            var parts = new List<string>();
            for (Transform t = item; t != null; t = t.parent)
            {
                parts.Add(t.name);
                if (t == root) break;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private void FCScanRealPolice()
        {
            if (!fcRealPoliceEnabled) return;
            FCEnsureSpawnRegistration();
            // Nur einmal bereits vorhandene Controller suchen. Neue Controller
            // kommen anschliessend ueber den Start-Hook in die Warteschlange.
            if (!fcInitialDiscoveryDone)
            {
                fcInitialDiscoveryDone = true;
                Type type = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightController");
                if (type != null)
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
                        if (obj is Component component && component.gameObject.scene.IsValid())
                            FCQueueController(component);
            }
            // Maximal ein Kandidat pro Frame; keine grossen Registrierungs-Bursts.
            if (fcPendingControllers.Count == 0 || Time.unscaledTime < fcNextPendingProcess) return;
            fcNextPendingProcess = Time.unscaledTime + 0.05f;
            Component controller = fcPendingControllers.Dequeue();
            if (controller == null) return;
            fcPendingIds.Remove(controller.GetInstanceID());
            try
            {
                // Registrierung pro Controller nur einmal, auch bei erneutem Start()-Event.
                var watch = System.Diagnostics.Stopwatch.StartNew();
                FCRegisterPoliceController(controller);
                watch.Stop();
                fcProcessedPoliceIds.Add(controller.GetInstanceID());
                if (watch.ElapsedMilliseconds >= 8)
                    UnityEngine.Debug.LogWarning("[FireControl INIT] Polizei " + controller.name + ": " + watch.ElapsedMilliseconds + " ms; Polizei-Warteschlange=" + fcPendingControllers.Count);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[FireControl] Polizeifahrzeug-Registrierung: " + ex.Message);
            }
        }

        private void FCRegisterPoliceController(Component controller)
        {
            if (controller == null || !controller.gameObject.scene.IsValid()) return;
            Transform? root = FCFindCarRoot(controller.transform);
            if (root == null || !FCIsPolice(root)) return;
            foreach (FCRealPoliceFlash existing in fcRealPolice)
                if (existing.Root == root) return;
            if (fcVehiclePrefab == null) FCLoadVehicle();
            if (fcVehiclePrefab == null) return;
            Type controllerType = controller.GetType();
            MethodInfo? getIndex = AccessTools.Method(controllerType, "GetLightIndex", new[] { typeof(string) });
            PropertyInfo? maskProperty = AccessTools.Property(controllerType, "Mask");
            FieldInfo? maskField = FCNativeField(controllerType, "Mask");
            if (getIndex == null || (maskProperty == null && maskField == null)) return;
            blueMaskProperty = maskProperty;
            blueMaskField = maskField;
            Transform? sourceVisuals = fcVehiclePrefab.transform.Find("Visuals");
            if (sourceVisuals == null) return;
                    Transform? targetVisuals = root.Find("Visuals");
                    if (targetVisuals == null) return;
                    int blueMask = 0;
                    foreach (string name in new[] { "Blaulicht", "Heckblaulicht" })
                    {
                        object? value = getIndex.Invoke(controller, new object[] { name });
                        if (value is int index && index >= 0 && index < 31) blueMask |= 1 << index;
                    }
                    if (blueMask == 0) return;
                    var entry = new FCRealPoliceFlash { Root = root, Controller = controller, BlueMask = blueMask };
                    // Alle Visuals uebernehmen, nicht nur Frontblitzer oder FC_STATIC_ADDONS.
                    FCMergeVisualTree(sourceVisuals, targetVisuals, entry);
                    // Weitere rein visuelle Prefab-Kinder ausserhalb von Visuals koennen
                    // unter FC_STATIC_ADDONS abgelegt werden; keine Fahrzeug-Scripts klonen.
                    Transform? staticSource = fcVehiclePrefab.transform.Find("FC_STATIC_ADDONS");
                    if (staticSource != null)
                    {
                        Transform? staticTarget = root.Find("FC_STATIC_ADDONS");
                        if (staticTarget == null)
                        {
                            FCCopyVisualTree(staticSource, root);
                            staticTarget = root.Find("FC_STATIC_ADDONS");
                            if (staticTarget != null) entry.Addons.Add(staticTarget.gameObject);
                        }
                        else FCMergeVisualTree(staticSource, staticTarget, entry);
                    }
                    // Die im POL_FC-Prefab vorhandene RTW-Matrix auch auf das
                    // echte Fahrzeug uebernehmen, falls dort noch keine existiert.
                    // Originale Matrix-Komponenten/Animationen bleiben erhalten.
                    Transform? matrix = root.Find("Matrix");
                    Transform? sourceMatrix = fcVehiclePrefab.transform.Find("Matrix");
                    if (matrix == null && sourceMatrix != null)
                    {
                        GameObject copiedMatrix = UnityEngine.Object.Instantiate(sourceMatrix.gameObject, root, false);
                        copiedMatrix.name = sourceMatrix.name;
                        matrix = copiedMatrix.transform;
                        entry.Addons.Add(copiedMatrix);
                    }
                    entry.MatrixRoot = matrix;
                    FCSetupPoliceMatrix(entry);
                    // Native Spielkurven statt eigener Emissions- und Blinksteuerung.
                    FCInstallNativePoliceLights(entry, fcVehiclePrefab.transform);
                    fcRealPolice.Add(entry);
                    FCUpdatePoliceMatrix(entry);

            fcVehicleStatus = "Polizeifahrzeuge registriert: " + fcRealPolice.Count;
        }

        // Die Spiel-DLL verwaltet Gruppen als Dictionary<string, CarLight[]>.
        // Nur vorhandene Gruppen ergaenzen: lightNames/lightIndices und Mask-Bits
        // bleiben dadurch unveraendert (wichtig fuer Spiel und Netzwerk).
        private static readonly BindingFlags FCNativeFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Transform? FCNativeResolve(Transform source, Transform prefabRoot, Transform targetRoot)
        {
            var parts = new List<string>();
            for (Transform? t = source; t != null && t != prefabRoot; t = t.parent)
                parts.Add(t.name);
            if (source != prefabRoot && !FCNativeIsDescendant(source, prefabRoot)) return null;
            Transform? result = targetRoot;
            for (int i = parts.Count - 1; i >= 0 && result != null; i--)
                result = result.Find(parts[i]);
            return result;
        }

        private static bool FCNativeIsDescendant(Transform item, Transform ancestor)
        {
            for (Transform? t = item; t != null; t = t.parent)
                if (t == ancestor) return true;
            return false;
        }

        private static FieldInfo? FCNativeField(Type type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField(name, FCNativeFlags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static void FCNativeCopySerialized(Component source, Component target)
        {
            for (Type? t = source.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(FCNativeFlags | BindingFlags.DeclaredOnly))
                {
                    if (f.IsStatic || f.IsInitOnly || f.Name == "lightMeshes" || f.Name == "lights") continue;
                    if (!f.IsPublic && !Attribute.IsDefined(f, typeof(SerializeField))) continue;
                    f.SetValue(target, f.GetValue(source));
                }
        }

        private static IEnumerable<string> FCNativeTableKeys(System.Collections.IDictionary table)
        {
            foreach (object key in table.Keys) yield return Convert.ToString(key) ?? "<null>";
        }

        // Automatische Zuordnung fuer aus Unity exportierte CarLightCurve-Komponenten,
        // deren AbstractLight.lightMeshes im Bundle nicht serialisiert wurde.
        // Nur Renderer im Visuals-Zweig und nur explizit benannte Blaulicht-Meshes.
        private static string FCNativeNormalize(string value)
        {
            return (value ?? "").ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
        }

        private static bool FCNativeBlueRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            string name = FCNativeNormalize(renderer.name);
            if (name.Contains("blaulicht") || name.Contains("frontblitz") ||
                name.Contains("heckblitz") || name.Contains("emergency") ||
                name.Contains("strobo") || name.Contains("blitzer")) return true;
            return false;
        }

        private static int FCNativeMatchScore(string sourceName, string rendererName)
        {
            string src = FCNativeNormalize(sourceName);
            string dst = FCNativeNormalize(rendererName);
            if (!FCNativeBlueRendererName(dst)) return -100;
            bool roof = src.Contains("blaulicht") && !src.Contains("frontblitz");
            bool plate = src.Contains("nummernschild");
            bool side = src.Contains("seitlich");
            bool rear = src.Contains("hinten") || src.Contains("heck");
            bool front = src.Contains("frontblitz") && !rear;
            bool dstRoof = dst.Contains("blaulicht");
            bool dstSide = dst.Contains("seitlich") || dst.Contains("side");
            bool dstRear = dst.Contains("hinten") || dst.Contains("heck") || dst.Contains("rear") || dst.Contains("emergencyb") || dst.Contains("blitzerb");
            bool dstFront = dst.Contains("front") || dst.Contains("emergencyf") || dst.Contains("blitzerf");
            bool dstPlate = dst.Contains("nummernschild") || dst.Contains("plate") || dst.Contains("kennzeichen");
            int score = 0;
            if (roof)
            {
                if (!dstRoof) return -100;
                score += 12;
                bool left = src.EndsWith("l") || src.EndsWith("links");
                bool right = src.EndsWith("r") || src.EndsWith("rechts");
                if (left && (dst.EndsWith("l") || dst.Contains("links"))) score += 14;
                else if (right && (dst.EndsWith("r") || dst.Contains("rechts"))) score += 14;
                else if (left || right) return -100;
            }
            else
            {
                if (dstRoof) return -100;
                if (rear) { if (!dstRear) return -100; score += 12; }
                else if (front) { if (!dstFront || dstRear) return -100; score += 12; }
                if (side) { if (dstSide) score += 12; else return -100; }
                else if (dstSide) score -= 6;
                if (plate) { if (dstPlate) score += 12; else score -= 5; }
                else if (dstPlate) score -= 4;
            }
            if (src == dst || dst.Contains(src)) score += 20;
            return score;
        }

        private static bool FCNativeBlueRendererName(string name)
        {
            return name.Contains("blaulicht") || name.Contains("frontblitz") ||
                name.Contains("heckblitz") || name.Contains("emergency") ||
                name.Contains("strobo") || name.Contains("blitzer");
        }

        // Pfadunabhaengige Suche: findet alle gleichnamigen Gruppen unter Visuals,
        // auch unter animierten Tueren, Heckklappen und beliebigen Zwischenobjekten.
        private static List<Transform> FCFindLightGroups(Transform root, string name)
        {
            var result = new List<Transform>();
            if (root == null || string.IsNullOrWhiteSpace(name)) return result;
            string wanted = name.Trim();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    result.Add(t);
            return result;
        }

        // V3: Zuordnung ueber den gesamten Hierarchiepfad statt nur renderer.name.
        // Bei den Emergency-Gruppen die aktivierten Kinder nehmen; die
        // Gruppen-Renderer selbst sind laut Diagnose deaktiviert.
        private static Renderer[] FCNativeAutoMeshes(Component source, Transform targetRoot, HashSet<Renderer> occupied)
        {
            Transform? visuals = targetRoot.Find("Visuals");
            if (visuals == null) return new Renderer[0];
            string src = FCNativeNormalize(source.name);
            string? group = null;
            bool roof = src.Contains("blaulicht") && !src.Contains("frontblitz");
            if (roof)
            {
                if (src.EndsWith("l") || src.EndsWith("links")) group = "L_blaulicht_L";
                else if (src.EndsWith("r") || src.EndsWith("rechts")) group = "L_blaulicht_R";
            }
            else if (src.Contains("nummernschild") && (src.Contains("hinten") || src.Contains("heck")))
                group = "L_emergency_B_2_blue";
            else if (src.Contains("nummernschild"))
                group = "L_emergency_F_2_blue";
            else if (src.Contains("seitlich"))
                group = "L_emergency_F_3_blue"; // vorlaeufige Zuordnung: im Spiel pruefen
            else if (src.Contains("frontblitz"))
                group = "L_emergency_F_blue";
            if (group == null) return new Renderer[0];

            var found = new List<Renderer>();
            foreach (Transform node in FCFindLightGroups(visuals, group))
            {
                foreach (Renderer r in node.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || occupied.Contains(r)) continue;
                    if (!r.gameObject.activeInHierarchy) continue;
                    if (!found.Contains(r)) found.Add(r);
                }
            }
            return found.ToArray();
        }

        // Diagnose vor der Zuordnung: zeigt auch deaktivierte und nicht passend benannte Renderer.
        // Dadurch sehen wir, ob die Visuals ueberhaupt angekommen sind und welche Namen sie haben.
        private void FCNativeDumpBlueRenderers(Transform targetRoot)
        {
            Transform? visuals = targetRoot.Find("Visuals");
            if (visuals == null)
            {
                ;
                return;
            }
            Renderer[] renderers = visuals.GetComponentsInChildren<Renderer>(true);
            ;
            int shown = 0;
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                string normalized = FCNativeNormalize(r.name);
                // Auch L_...blue erfassen: solche Namen wurden bisher uebersehen.
                bool candidate = FCNativeBlueRendererName(normalized) ||
                    normalized.Contains("blue") || normalized.Contains("blau") ||
                    normalized.Contains("light") || normalized.Contains("licht");
                if (!candidate) continue;
                ;
                if (++shown >= 100)
                {
                    ;
                    break;
                }
            }
            ;
        }

        private static int FCNativeSpecificity(string name)
        {
            string n = FCNativeNormalize(name);
            return (n.Contains("hinten") ? 10 : 0) +
                   (n.Contains("nummernschild") ? 10 : 0) +
                   (n.Contains("seitlich") ? 10 : 0) +
                   (n.Contains("blaulicht") ? 5 : 0);
        }

        // Native Spiel-Kurven zur Laufzeit erzeugen: funktioniert auch mit leerer
        // AssetRipper-Dummyklasse CarLightCurve im Unity-Editor.
        private static AnimationCurve FCPoliceFlashCurve(int phase)
        {
            // Harte Schaltkanten statt der originalen Dreieckskurve (weisses Auf-/Abdimmen).
            float offset = (phase % 2) * 0.24f;
            float[] times = { 0f, .035f, .080f, .115f, .160f, .195f, .48f };
            float[] values = { 0f, 1f, 0f, 1f, 0f, 0f, 0f };
            var keys = new List<Keyframe>();
            for (int i = 0; i < times.Length; i++)
            {
                float t = times[i] + offset;
                if (t >= 0.96f) continue;
                keys.Add(new Keyframe(t, values[i]));
            }
            keys.Add(new Keyframe(1f, 0f));
            // Constant tangents: echte harte Blitze, keine linearen Rampen.
            for (int i = 0; i < keys.Count; i++)
            {
                Keyframe k = keys[i];
                k.inTangent = float.PositiveInfinity;
                k.outTangent = float.PositiveInfinity;
                keys[i] = k;
            }
            AnimationCurve curve = new AnimationCurve(keys.ToArray());
            curve.preWrapMode = WrapMode.Loop;
            curve.postWrapMode = WrapMode.Loop;
            return curve;
        }

        // Konfigurierbare, voneinander entkoppelte Warnlichtmuster (keine OEM-Kurven).
        // Eine gemeinsame 2.4-s-Periode mit unterschiedlichen Impulspositionen.
        private static AnimationCurve FCIndependentFlashCurve(string name, int side)
        {
            string n = name.ToLowerInvariant();
            float[] starts;
            float width;
            if (n.Contains("heckabsicherung")) { starts = side == 0 ? new float[] { .10f, .27f } : new float[] { 1.28f, 1.45f }; width = .075f; }
            else if (n.Contains("seitlich")) { starts = new float[] { .54f, .69f, 1.73f, 1.88f }; width = .055f; }
            else if (n.Contains("nummernschild_hinten")) { starts = new float[] { .90f, 1.07f }; width = .065f; }
            else if (n.Contains("nummernschild")) { starts = new float[] { .20f, .36f, 1.38f, 1.54f }; width = .055f; }
            else { starts = new float[] { .04f, .14f, .25f, 1.16f, 1.26f, 1.37f }; width = .045f; }
            var keys = new List<Keyframe> { new Keyframe(0f, 0f) };
            foreach (float t in starts)
            {
                keys.Add(new Keyframe(t, 1f));
                keys.Add(new Keyframe(t + width, 0f));
            }
            keys.Add(new Keyframe(2.4f, 0f));
            for (int i = 0; i < keys.Count; i++)
            {
                Keyframe k = keys[i];
                k.inTangent = float.PositiveInfinity;
                k.outTangent = float.PositiveInfinity;
                keys[i] = k;
            }
            var curve = new AnimationCurve(keys.ToArray());
            curve.preWrapMode = WrapMode.Loop;
            curve.postWrapMode = WrapMode.Loop;
            return curve;
        }

        private static Renderer[] FCPoliceRenderers(Transform root, string group,
            HashSet<Renderer> occupied)
        {
            Transform visuals = root.Find("Visuals");
            var result = new List<Renderer>();
            if (visuals == null) return result.ToArray();
            foreach (Transform node in FCFindLightGroups(visuals, group))
            {
                foreach (Renderer renderer in node.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || occupied.Contains(renderer) || !renderer.enabled) continue;
                    if (!result.Contains(renderer)) result.Add(renderer);
                }
            }
            return result.ToArray();
        }

        // Frontblitzer-only: eigene Materialinstanzen, damit die Emissionstextur
        // die per originalem CarLightCurve gesetzte blaue _EmissionColor nicht
        // mit Rot/Pink multipliziert. Originalmaterialien bleiben unveraendert.
        private void FCPrepareFrontFlashMaterials(Renderer[] renderers)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                try
                {
                    Material[] materials = renderer.materials; // Instanzen nur fuer diesen Renderer
                    int changed = 0;
                    foreach (Material material in materials)
                    {
                        if (material == null || !material.HasProperty("_EmissionColor")) continue;
                        material.EnableKeyword("_EMISSION");
                        if (material.HasProperty("_EmissionMap"))
                            material.SetTexture("_EmissionMap", null);
                        // Grundwert wird durch das originale Spiel mit
                        // MaterialPropertyBlock bei jedem Blinkimpuls ueberschrieben.
                        material.SetColor("_EmissionColor", Color.black);
                        changed++;
                    }
                    ;
                }
                catch (Exception)
                {
                    ;
                }
            }
        }

        // Universelle POL_FC-Registrierung: echte Renderer-Referenzen haben Vorrang.
        // Nicht eindeutig aufloesbare Leuchten werden protokolliert statt falsch verdrahtet.
        private static string FCAutoPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (Transform n = t; n != null && n != root; n = n.parent) parts.Insert(0, n.name.Trim());
            return string.Join("/", parts.ToArray());
        }

        private static Transform? FCAutoResolve(Transform root, string path)
        {
            Transform current = root;
            foreach (string part in path.Split('/'))
            {
                Transform? next = null;
                foreach (Transform child in current)
                    if (string.Equals(child.name.Trim(), part.Trim(), StringComparison.OrdinalIgnoreCase))
                    { next = child; break; }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        private static Renderer[] FCAutoMapRenderers(Component donor, Transform prefabRoot,
            Transform liveRoot, FieldInfo meshesField, HashSet<Renderer> occupied,
            string legacyGroup, StringBuilder report)
        {
            var result = new List<Renderer>();
            Renderer[]? references = meshesField.GetValue(donor) as Renderer[];
            if (references != null && references.Length > 0)
            {
                foreach (Renderer original in references)
                {
                    if (original == null || !original.transform.IsChildOf(prefabRoot)) continue;
                    string path = FCAutoPath(original.transform, prefabRoot);
                    Transform? resolved = FCAutoResolve(liveRoot, path);
                    Renderer? live = resolved == null ? null : resolved.GetComponent(original.GetType()) as Renderer;
                    if (live != null && !occupied.Contains(live) && !result.Contains(live)) result.Add(live);
                    else report.AppendLine("REFERENZ NICHT AUFGELOEST | " + donor.name + " | " + path);
                }
                if (result.Count > 0) return result.ToArray();
            }
            // Explizit benannte Standardgruppen sind ein kompatibler Fallback.
            if (!string.IsNullOrEmpty(legacyGroup))
                foreach (Renderer r in FCPoliceRenderers(liveRoot, legacyGroup, occupied))
                    if (!result.Contains(r)) result.Add(r);
            if (result.Count > 0) return result.ToArray();

            // Generische, priorisierte Zuordnung: nur die engste Leuchtengruppe
            // auswerten, nicht uebergeordnete Tueren/Visuals mit allen Renderern.
            // Bei fehlenden serialisierten lightMeshes werden Name, Einbauort und
            // Materialnamen kombiniert. Keine mehrdeutigen Gruppen blind verbinden.
            Transform visuals = liveRoot.Find("Visuals");
            if (visuals == null) return result.ToArray();
            string control = FCNativeNormalize(donor.name);
            bool rear = control.Contains("heck") || control.Contains("rear") || control.Contains("back");
            bool warning = control.Contains("warn") || control.Contains("absicherung") || control.Contains("warning");
            var ranked = new List<Transform>();
            int best = 0;
            foreach (Transform t in visuals.GetComponentsInChildren<Transform>(true))
            {
                string n = FCNativeNormalize(t.name);
                if (!n.StartsWith("l") || !(n.Contains("warning") || n.Contains("warn") || n.Contains("emergency") || n.Contains("blitz"))) continue;
                // Nur Leuchtengruppen, nicht einzelne Frontblitzer oder deren Eltern.
                if (n.Contains("frontblitzer") || n.Contains("heckblitzer")) continue;
                Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
                bool usable = false;
                foreach (Renderer r in renderers)
                    if (r != null && r.enabled && !occupied.Contains(r)) { usable = true; break; }
                if (!usable) continue;
                string path = FCNativeNormalize(FCAutoPath(t, visuals));
                int score = 0;
                if (warning && (n.Contains("warning") || n.Contains("warn"))) score += 12;
                // LOD0/LOD1 sind Karosserie-/Darstellungsgruppen, keine sichere
                // Zuordnung zu den eigentlichen Warnleuchten. Nicht blind verbinden.
                if (n.Contains("lod") || n.Contains("body") || n.Contains("mesh")) score -= 30;
                // Eigenstaendige Leuchtengruppen mit Farbkennzeichnung bevorzugen.
                if (n.Contains("orange") || n.Contains("amber") || n.Contains("gelb") || n.Contains("yellow")) score += 10;
                if (n.Contains("blue") || n.Contains("blau")) score += warning ? -8 : 0;
                // Der eigentliche Leuchtenträger liegt oft unter einer animierten Tuer.
                // Das ist eine generische Hierarchie-Eigenschaft, kein Fahrzeug-Sonderfall.
                if (path.Contains("door") && !n.Contains("lod")) score += 3;
                if (warning && n.Contains("emergency")) score -= 8;
                if (!warning && n.Contains("emergency")) score += 6;
                if (rear && (path.Contains("doorb") || path.Contains("rear") || path.Contains("heck"))) score += 8;
                if (rear && (path.Contains("doorlf") || path.Contains("doorrf"))) score -= 6;
                if (control.Contains("rot") && (n.Contains("red") || n.Contains("rot"))) score += 4;
                if (control.Contains("orange") && n.Contains("orange")) score += 4;
                // Auch bei abweichender Farbbenennung (Rot/Orange) nicht ausschliessen.
                if (score <= 0) continue;
                report.AppendLine("KANDIDAT | " + donor.name + " | " + FCAutoPath(t, visuals) + " | Score=" + score);
                if (score > best) { best = score; ranked.Clear(); }
                if (score == best) ranked.Add(t);
            }
            if (ranked.Count == 0)
                report.AppendLine("KEINE VISUAL-KANDIDATEN | " + donor.name);
            else
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Transform t in ranked) names.Add(t.name.Trim());
                if (names.Count == 1)
                {
                    foreach (Transform t in ranked)
                        foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                            if (r != null && r.enabled && !occupied.Contains(r) && !result.Contains(r)) result.Add(r);
                    report.AppendLine("AUTO-MATCH | " + donor.name + " | Gruppe=" + string.Join(",", new List<string>(names).ToArray()) + " | Score=" + best + " | Renderer=" + result.Count);
                }
                else report.AppendLine("MEHRDEUTIGE VISUALS | " + donor.name + " | " + string.Join(",", new List<string>(names).ToArray()));
            }
            return result.ToArray();
        }

        private void FCInstallNativePoliceLights(FCRealPoliceFlash entry, Transform prefabRoot)
        {
            var report = new StringBuilder();
            report.AppendLine("FIRE CONTROL - AUTOMATISCHE CARLIGHT REGISTRIERUNG");
            report.AppendLine("Zeit: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("Fahrzeug: " + entry.Root.name);
            try
            {
                Type curveType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightCurve");
                if (curveType == null) { report.AppendLine("FEHLER | CarLightCurve-Typ fehlt"); return; }
                FieldInfo? tableField = FCNativeField(entry.Controller.GetType(), "carLights");
                FieldInfo? meshesField = FCNativeField(curveType, "lightMeshes");
                FieldInfo? lightsField = FCNativeField(curveType, "lights");
                FieldInfo? kindField = FCNativeField(curveType, "lightType");
                if (tableField == null || meshesField == null || lightsField == null || kindField == null)
                { report.AppendLine("FEHLER | CarLightController API unvollstaendig"); return; }
                var table = tableField.GetValue(entry.Controller) as System.Collections.IDictionary;
                if (table == null) { report.AppendLine("FEHLER | carLights-Tabelle fehlt"); return; }
                var occupied = new HashSet<Renderer>();
                foreach (System.Collections.DictionaryEntry pair in table)
                    if (pair.Value is Array array)
                        foreach (object obj in array)
                            if (obj is Component c)
                            {
                                Renderer[]? existing = meshesField.GetValue(c) as Renderer[];
                                if (existing != null) foreach (Renderer r in existing) if (r != null) occupied.Add(r);
                            }
                var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "CarLight_Frontblitzer_Nummernschild_Hinten", "L_emergency_B_2_blue" },
                    { "CarLight_Frontblitzer_Seitlich", "L_emergency_F_3_blue" },
                    { "CarLight_Frontblitzer_Nummernschild", "L_emergency_F_2_blue" },
                    { "CarLight_Frontblitzer", "L_emergency_F_blue" }
                };
                var pending = new List<Component>();
                var pendingGroups = new List<string>();
                GameObject staging = new GameObject("FC_NATIVE_AUTO_LIGHTS");
                staging.SetActive(false);
                staging.transform.SetParent(entry.Controller.transform, false);
                entry.NativeObjects.Add(staging);
                Component[] donors = prefabRoot.GetComponentsInChildren(curveType, true);
                foreach (Component donor in donors)
                {
                    if (donor == null) continue;
                    string name = donor.name.Trim();
                    bool standard = legacy.ContainsKey(name);
                    string? fallback = standard ? legacy[name] : null;
                    // Schon im Originalcontroller vorhandene Namen nicht duplizieren.
                    bool duplicate = false;
                    foreach (System.Collections.DictionaryEntry pair in table)
                        if (pair.Value is Array arr)
                            foreach (object item in arr)
                                if (item is Component c && string.Equals(c.name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                                    duplicate = true;
                    if (duplicate) { report.AppendLine("ORIGINAL BELASSEN | " + name); continue; }
                    Renderer[] mapped = FCAutoMapRenderers(donor, prefabRoot, entry.Root, meshesField, occupied, fallback ?? "", report);
                    if (mapped.Length == 0) { report.AppendLine("NICHT ZUGEORDNET | " + name + " | Renderer fehlen oder mehrdeutig"); continue; }
                    string group = Convert.ToString(kindField.GetValue(donor))?.Trim() ?? "";
                    if (string.IsNullOrEmpty(group) || !table.Contains(group))
                    {
                        if (!table.Contains("Blaulicht")) { report.AppendLine("KEINE SCHALTGRUPPE | " + name + " | " + group); continue; }
                        report.AppendLine("GRUPPEN-FALLBACK | " + name + " | " + group + " -> Blaulicht");
                        group = "Blaulicht";
                    }
                    if (!(table[group] is Array)) { report.AppendLine("UNGUELTIGE SCHALTGRUPPE | " + name + " | " + group); continue; }
                    bool rearWarning = name.IndexOf("Heckabsicherung", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Bei Heckabsicherung jeden Renderer separat steuern, damit links/rechts wechseln.
                    int parts = rearWarning ? mapped.Length : 1;
                    MethodInfo assign = AccessTools.Method(curveType, "AssignCurve", new[] { typeof(AnimationCurve) });
                    if (assign == null) { report.AppendLine("FEHLER | AssignCurve fehlt | " + name); continue; }
                    for (int part = 0; part < parts; part++)
                    {
                        GameObject node = new GameObject(parts > 1 ? name + "_" + part : name);
                        node.transform.SetParent(staging.transform, false);
                        Component native = node.AddComponent(curveType);
                        FCNativeCopySerialized(donor, native);
                        kindField.SetValue(native, group);
                        Renderer[] targets = rearWarning ? new Renderer[] { mapped[part] } : mapped;
                        meshesField.SetValue(native, targets);
                        lightsField.SetValue(native, new Light[0]);
                        assign.Invoke(native, new object[] { FCIndependentFlashCurve(name, part % 2) });
                        if (rearWarning || standard ||
                            name.IndexOf("Rot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Orange", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Blau", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Blitz", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            FieldInfo? color = FCNativeField(curveType, "emissiveColor");
                            FieldInfo? strength = FCNativeField(curveType, "emissiveStrength");
                            if (color != null)
                            {
                                string lowerName = name.ToLowerInvariant();
                                Color selected = rearWarning || lowerName.Contains("orange") || lowerName.Contains("amber")
                                    ? new Color(1f, .43f, .005f, 1f)
                                    : lowerName.Contains("rot") || lowerName.Contains("red")
                                        ? new Color(1f, .025f, .008f, 1f)
                                        : new Color(.10f, .43f, 1f, 1f);
                                color.SetValue(native, selected);
                            }
                            if (strength != null) strength.SetValue(native, 10f);
                            FCPrepareFrontFlashMaterials(targets);
                        }
                        pending.Add(native);
                        pendingGroups.Add(group);
                        report.AppendLine("ASYNC-BLITZ | " + name + " | Teil=" + part + " | Renderer=" + targets.Length + " | Periode=2.4s");
                    }
                    foreach (Renderer r in mapped) occupied.Add(r);
                    report.AppendLine("REGISTRIERT | " + name + " | Gruppe=" + group + " | Renderer=" + mapped.Length + " | Modus=Asynchron");
                }
                for (int i = 0; i < pending.Count; i++)
                {
                    string group = pendingGroups[i];
                    Array? old = table[group] as Array;
                    if (old == null) continue;
                    if (!entry.NativeOriginalGroups.ContainsKey(group)) entry.NativeOriginalGroups[group] = old;
                    Array combined = Array.CreateInstance(old.GetType().GetElementType()!, old.Length + 1);
                    Array.Copy(old, combined, old.Length);
                    combined.SetValue(pending[i], old.Length);
                    table[group] = combined;
                }
                staging.SetActive(true);
                FieldInfo? maskField = FCNativeField(entry.Controller.GetType(), "Mask");
                PropertyInfo maskProperty = entry.Controller.GetType().GetProperty("Mask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? rawMask = maskProperty != null ? maskProperty.GetValue(entry.Controller, null) : maskField?.GetValue(entry.Controller);
                int mask = rawMask == null ? 0 : Convert.ToInt32(rawMask);
                MethodInfo getIndex = AccessTools.Method(entry.Controller.GetType(), "GetLightIndex", new Type[] { typeof(string) });
                for (int i = 0; i < pending.Count; i++)
                {
                    object? index = getIndex?.Invoke(entry.Controller, new object[] { pendingGroups[i] });
                    bool on = index is int idx && idx >= 0 && idx < 31 && (mask & (1 << idx)) != 0;
                    if (pending[i] is Behaviour behaviour) behaviour.enabled = on;
                }
                report.AppendLine("GESAMT REGISTRIERT: " + pending.Count);
            }
            catch (Exception ex) { report.AppendLine("FEHLER | " + ex); ; }
            finally
            {
                try {
                    string folder = Path.Combine(Paths.ConfigPath, "FireControl");
                    Directory.CreateDirectory(folder);
                    ;
                    ;
                } catch (Exception) { ; }
            }
        }

        // Universelle Zusatzleuchten: auf JEDEM Fahrzeug mit CarLightController.
        // Konvention: LightSignals/Controllable/Blaulicht/FC_AUTO_<Name>
        // und Visuals/FC_AUTO_<Name>. Nur explizit markierte Leuchten werden angefasst.
        private readonly Dictionary<Component, HashSet<string>> fcAutoInstalled =
            new Dictionary<Component, HashSet<string>>();
        private void FCScanAutoLights()
        {
            // Pro Update hoechstens einen neuen Controller bearbeiten.
            // Keine globale FindObjectsByType-Suche mehr.
            if (fcAutoPendingControllers.Count == 0) return;
            Component queuedController = fcAutoPendingControllers.Dequeue();
            if (queuedController == null || !queuedController.gameObject.scene.IsValid()) return;
            var autoWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Type controllerType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightController");
                Type curveType = AccessTools.TypeByName("com.crenetic.N2019Plugins.Vehicle.CarLightCurve");
                if (controllerType == null || curveType == null) return;
                FieldInfo? tableField = FCNativeField(controllerType, "carLights");
                FieldInfo? meshesField = FCNativeField(curveType, "lightMeshes");
                FieldInfo? lightsField = FCNativeField(curveType, "lights");
                FieldInfo? kindField = FCNativeField(curveType, "lightType");
                FieldInfo? colorField = FCNativeField(curveType, "emissiveColor");
                FieldInfo? strengthField = FCNativeField(curveType, "emissiveStrength");
                MethodInfo assign = AccessTools.Method(curveType, "AssignCurve", new[] { typeof(AnimationCurve) });
                if (tableField == null || meshesField == null || lightsField == null ||
                    kindField == null || colorField == null || strengthField == null || assign == null) return;
                foreach (Component controller in new Component[] { queuedController })
                {
                    Transform? root = FCFindCarRoot(controller.transform);
                    if (root == null) continue;
                    Transform visuals = root.Find("Visuals");
                    Transform group = controller.transform.Find("Blaulicht");
                    if (visuals == null || group == null) continue;
                    var table = tableField.GetValue(controller) as System.Collections.IDictionary;
                    if (table == null || !table.Contains("Blaulicht")) continue;
                    Array? original = table["Blaulicht"] as Array;
                    if (original == null) continue;
                    HashSet<string> installed;
                    if (!fcAutoInstalled.TryGetValue(controller, out installed))
                    {
                        installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        fcAutoInstalled.Add(controller, installed);
                    }
                    foreach (Transform marker in group)
                    {
                        if (!marker.name.StartsWith("FC_AUTO_", StringComparison.OrdinalIgnoreCase) ||
                            installed.Contains(marker.name)) continue;
                        var targets = FCFindLightGroups(visuals, marker.name);
                        if (targets.Count == 0) continue;
                        var meshList = new List<Renderer>();
                        foreach (Transform target in targets)
                            foreach (Renderer r in target.GetComponentsInChildren<Renderer>(true))
                                if (r != null && r.enabled && !meshList.Contains(r)) meshList.Add(r);
                        Renderer[] meshes = meshList.ToArray();
                        if (meshes.Length == 0) continue;
                        Component donor = marker.GetComponent(curveType);
                        if (donor == null)
                        {
                            ;
                            installed.Add(marker.name);
                            continue;
                        }
                        // Marker selbst bleibt unveraendert; eine neue native Komponente
                        // wird in die bestehende Blaulicht-Gruppe eingetragen.
                        GameObject node = new GameObject("FC_AUTO_RUNTIME_" + marker.name);
                        node.SetActive(false);
                        node.transform.SetParent(group, false);
                        try
                        {
                            Component native = node.AddComponent(curveType);
                            FCNativeCopySerialized(donor, native);
                            kindField.SetValue(native, "Blaulicht");
                            meshesField.SetValue(native, meshes);
                            lightsField.SetValue(native, new Light[0]);
                            // Originalkurve des Markers unveraendert lassen.
                            colorField.SetValue(native, new Color(.082f, .357f, 1f, 1f));
                            strengthField.SetValue(native, 10f);
                            FCPrepareFrontFlashMaterials(meshes);
                            Array? current = table["Blaulicht"] as Array;
                            if (current == null) { UnityEngine.Object.Destroy(node); continue; }
                             Array combined = Array.CreateInstance(current.GetType().GetElementType()!, current.Length + 1);
                            Array.Copy(current, combined, current.Length);
                            combined.SetValue(native, current.Length);
                            table["Blaulicht"] = combined;
                            node.SetActive(true);
                            // Originalcontroller entscheidet, ob die Gruppe gerade an ist.
                            MethodInfo indexMethod = AccessTools.Method(controllerType, "GetLightIndex", new[] { typeof(string) });
                            FieldInfo? maskField = FCNativeField(controllerType, "Mask");
                            PropertyInfo maskProperty = AccessTools.Property(controllerType, "Mask");
                            object? idxRaw = indexMethod == null ? null : indexMethod.Invoke(controller, new object[] { "Blaulicht" });
                            object? maskRaw = maskProperty != null ? maskProperty.GetValue(controller, null) :
                                maskField != null ? maskField.GetValue(controller) : null;
                            int idx = idxRaw is int ? (int)idxRaw : -1;
                            bool on = idx >= 0 && idx < 31 && maskRaw != null &&
                                (Convert.ToInt32(maskRaw) & (1 << idx)) != 0;
                            if (native is Behaviour behaviour) behaviour.enabled = on;
                            installed.Add(marker.name);
                            ;
                        }
                        catch (Exception)
                        {
                            UnityEngine.Object.Destroy(node);
                            installed.Add(marker.name);
                            ;
                        }
                    }
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[FireControl INIT] Zusatzlichter: " + ex.Message); }
            finally
            {
                autoWatch.Stop();
                if (autoWatch.ElapsedMilliseconds >= 8)
                    UnityEngine.Debug.LogWarning("[FireControl INIT] Zusatzlichter " + queuedController.name + ": " + autoWatch.ElapsedMilliseconds + " ms; Licht-Warteschlange=" + fcAutoPendingControllers.Count);
            }
        }

        // Die Matrix ist unabhaengig vom Blaulicht. Das physische Panel bleibt
        // bereits beim Spawn sichtbar. Die Cockpit-Taste steuert den nativen Controller.
        private void FCUpdatePoliceMatrix(FCRealPoliceFlash entry)
        {
            if (entry.MatrixRoot == null) return;
            if (!entry.MatrixRoot.gameObject.activeSelf) entry.MatrixRoot.gameObject.SetActive(true);
            if (entry.MatrixPanel != null && !entry.MatrixPanel.gameObject.activeSelf)
                entry.MatrixPanel.gameObject.SetActive(true);
            // Der originale Controller aktualisiert nachtraeglich angebaute Panels
            // nicht immer; deshalb nur das Matrixpanel erneut freigeben.
            // FCRefreshMatrixPanel wird einmalig in FCSetupPoliceMatrix aufgerufen.
            // Keine Renderer/Animationen abschalten: sonst verschwindet das Panel.
        }

        private void FCUpdateRealPolice()
        {
            // Der originale Controller schaltet die registrierten CarLightCurve-Komponenten.
            FCScanRealPolice();
            for (int i = fcRealPolice.Count - 1; i >= 0; i--)
            {
                if (fcRealPolice[i].Root == null || fcRealPolice[i].Controller == null)
                    fcRealPolice.RemoveAt(i);
                else
                    FCUpdatePoliceMatrix(fcRealPolice[i]);
            }
        }

        private void FCRemoveRealPolice()
        {
            foreach (FCRealPoliceFlash entry in fcRealPolice)
            {
                if (entry.Controller != null)
                {
                    FieldInfo? f = FCNativeField(entry.Controller.GetType(), "carLights");
                    System.Collections.IDictionary? table = f?.GetValue(entry.Controller) as System.Collections.IDictionary;
                    if (table != null)
                        foreach (var pair in entry.NativeOriginalGroups) table[pair.Key] = pair.Value;
                }
                foreach (GameObject node in entry.NativeObjects) if (node != null) Destroy(node);
                foreach (GameObject addon in entry.Addons) if (addon != null) Destroy(addon);
                foreach (Component component in entry.AddedComponents) if (component != null) Destroy(component);
            }
            foreach (FCVisualBackup backup in fcVisualBackups)
            {
                if (backup.Filter != null) backup.Filter.sharedMesh = backup.Mesh;
                if (backup.Renderer != null)
                {
                    if (backup.Materials != null) backup.Renderer.sharedMaterials = backup.Materials;
                    backup.Renderer.enabled = backup.RendererEnabled;
                }
            }
            fcVisualBackups.Clear();
            foreach (FCRealPoliceFlash entry in fcRealPolice)
                if (entry.MatrixPanel != null)
                    foreach (Component button in entry.MatrixPanel.GetComponentsInChildren(
                        AccessTools.TypeByName("LocalPanel.MatrixButton") ?? typeof(Transform), true))
                    {
                        fcMatrixButtons.Remove(button);
                        fcMatrixButtonEntries.Remove(button);
                    }
            fcRealPolice.Clear();
            fcPendingControllers.Clear();
            fcPendingIds.Clear();
            fcProcessedPoliceIds.Clear();
            fcInitialDiscoveryDone = false;
            fcAutoPendingControllers.Clear();
            fcAutoSeenIds.Clear();
            fcAutoInstalled.Clear();
            if (ReferenceEquals(fcSpawnOwner, this)) fcSpawnOwner = null;
        }




        // F9: einmaliger, nur lesender Diagnoseexport der ORIGINALEN GameDatabase-Items.
        // Keine Prefab-Instanziierung, keine Manipulation an Fahrzeugen oder Inventaren.


        private static string FCItemRelativePath(Transform root, Transform current)
        {
            if (current == root) return ".";
            var names = new List<string>();
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }


        // F11: nicht-invasive Nachverfolgung der originalen Q->Pickup-Aufrufkette.
        private Harmony? fcPickupTraceHarmony;
        private static ModMenu? fcPickupTraceInstance;








        // Nur das von Fire Control erzeugte Original-Item im POL_FC pruefen.
        // Der native Gate.IsOpen-Status wird nach Ende der Oeffnungsanimation gesetzt.
        private void FCInstallPickupTrace()
        {
            fcPickupTraceInstance = this;
            try
            {
                fcPickupTraceHarmony = new Harmony("de.notruf112.firecontrol.pickup-native");
                Type focus = AccessTools.TypeByName("com.crenetic.Notruf2019.Interact.InteractionFocus");
                Type avatar = AccessTools.TypeByName("com.crenetic.N2019Plugins.Character.Avatar");
                MethodInfo canPickup = focus != null ? AccessTools.Method(focus, "CanPickup") : null;
                if (canPickup != null)
                    fcPickupTraceHarmony.Patch(canPickup, postfix: new HarmonyMethod(typeof(ModMenu), nameof(FCGateCanPickup)));
                if (avatar != null)
                    foreach (MethodInfo method in avatar.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name != "Pickup") continue;
                        ParameterInfo[] pars = method.GetParameters();
                        if (pars.Length == 0 || !typeof(Item).IsAssignableFrom(pars[0].ParameterType)) continue;
                        fcPickupTraceHarmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(ModMenu), nameof(FCGateAvatarPickup)),
                            postfix: new HarmonyMethod(typeof(ModMenu), nameof(FCTraceAvatarPickupAfter)));
                    }
            }
            catch { }
        }

        private static bool FCExtinguisherTrunkOpen(Item item)
        {
            ModMenu mod = fcPickupTraceInstance;
            if (mod == null || item == null) return true;
            foreach (var entry in mod.fcRealPolice)
            {
                Transform car = entry.Root;
                if (car == null) continue;
                Transform registered;
                if (!mod.fcExtinguisherWorldItems.TryGetValue(car.GetInstanceID(), out registered) ||
                    registered == null || registered != item.transform) continue;
                Transform door = car.Find("Visuals/Door_B");
                if (door == null) { mod.FCLogMissingTrunkGate(car, "Door_B fehlt"); return false; }
                Component gate = door.GetComponent("Gate");
                if (gate == null) { mod.FCLogMissingTrunkGate(car, "Gate fehlt"); return false; }
                object open = FCReadMemberSilent(gate, "IsOpen");
                if (!(open is bool)) { mod.FCLogMissingTrunkGate(car, "Gate.IsOpen unlesbar"); return false; }
                return (bool)open;
            }
            return true; // Alle anderen Items unangetastet lassen.
        }
        private readonly HashSet<int> fcMissingTrunkGateLogged = new HashSet<int>();
        private void FCLogMissingTrunkGate(Transform car, string reason)
        {
            if (fcMissingTrunkGateLogged.Add(car.GetInstanceID()))
                { } // logging disabled
        }
        // Sperrt die native Fokus-Aktion und damit die Q-Aufnahme bei geschlossener Klappe.
        private static void FCGateCanPickup(object __instance, object[] __args, ref bool __result)
        {
            Item item = null;
            if (__args != null) foreach (object a in __args) if (a is Item) { item = (Item)a; break; }
            if (item != null && !FCExtinguisherTrunkOpen(item)) __result = false;
        }
        // Zweite Sicherung: auch direkte Avatar.Pickup-Aufrufe duerfen nicht umgehen.
        private static bool FCGateAvatarPickup(object __instance, object[] __args)
        {
            Item item = __args != null && __args.Length > 0 ? __args[0] as Item : null;
            if (item != null && !FCExtinguisherTrunkOpen(item)) return false;
            return true;
        }



        // F13: protokolliert echte GOAP-Entscheidungen, ohne Spielentscheidungen zu ueberschreiben.




        // F12: originalen Aufruf beobachten, ohne den Pickup selbst zu veraendern.
        private static void FCTraceAvatarPickupAfter(object __instance, object[] __args)
        {
            if (fcPickupTraceInstance == null) return;
            Item item = __args != null && __args.Length > 0 ? __args[0] as Item : null;
            if (item == null) return;
            // Nur die von Fire Control erzeugten POL-Feuerloescher behandeln.
            bool ours = false;
            foreach (Transform t in fcPickupTraceInstance.fcExtinguisherWorldItems.Values)
                if (t != null && t == item.transform) { ours = true; break; }
            if (!ours) return;
            fcPickupTraceInstance.StartCoroutine(fcPickupTraceInstance.FCNativePickupFallback(item, __instance));
        }

        // Native Item-Sequenz als eng begrenzter Fallback, wenn Avatar.Pickup
        // den Item-Zustand nicht veraendert. Kein eigener Ausruestungszustand.
        private readonly HashSet<int> fcPickupFallbackRunning = new HashSet<int>();
        private IEnumerator FCNativePickupFallback(Item item, object avatar)
        {
            if (item == null || !FCExtinguisherTrunkOpen(item)) yield break;
            int id = item.GetInstanceID();
            if (!fcPickupFallbackRunning.Add(id)) yield break;
            yield return new WaitForSeconds(0.35f);
            try
            {
                if (item == null || !FCExtinguisherTrunkOpen(item) || item.Owner != null || FCReadMemberSilent(item, "nextOwner") != null) yield break;
                IItemOwner owner = avatar as IItemOwner;
                if (owner == null)
                {
                    { } // logging disabled
                    yield break;
                }
                if (!item.CanPickup(true) || item.IsAttachedToStatic())
                {
                    { } // logging disabled
                    yield break;
                }
                { } // logging disabled
                yield return StartCoroutine(item.PickupSequence(owner, null));
                { } // logging disabled
            }
            finally { fcPickupFallbackRunning.Remove(id); }
        }



        private static object FCReadMemberSilent(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                for (Type t = obj.GetType(); t != null; t = t.BaseType)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    PropertyInfo p = t.GetProperty(name, flags);
                    if (p != null) return p.GetValue(obj, null);
                    FieldInfo f = t.GetField(name, flags);
                    if (f != null) return f.GetValue(obj);
                }
            }
            catch (Exception ex) { return "ERR:" + ex.GetType().Name; }
            return "UNAVAILABLE";
        }



        private readonly System.Collections.Generic.Dictionary<int, Transform> fcExtinguisherWorldItems = new System.Collections.Generic.Dictionary<int, Transform>();
        private float fcExtinguisherNextScan;
        // Rueckgabe nur nach tatsaechlicher Entnahme; kein Teleport beim freien Ablegen.
        private readonly HashSet<int> fcExtinguisherWasHeld = new HashSet<int>();
        private readonly HashSet<int> fcExtinguisherReturned = new HashSet<int>();
        private readonly Dictionary<int, float> fcExtinguisherReturnCandidateSince = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> fcExtinguisherReturnCandidatePos = new Dictionary<int, Vector3>();
        private const float FC_EXT_RETURN_RADIUS = 0.85f;
        private const float FC_EXT_RETURN_SETTLE_SECONDS = 0.4f;
        private readonly System.Collections.Generic.HashSet<int> fcExtinguisherFailures = new System.Collections.Generic.HashSet<int>();

        private void FCInstallOriginalExtinguisher()
        {
            if (Time.unscaledTime < fcExtinguisherNextScan) return;
            fcExtinguisherNextScan = Time.unscaledTime + 6f;
            try
            {
                Type dbType = AccessTools.TypeByName("com.crenetic.N2019Plugins.GameUnits.GameDatabase");
                if (dbType == null) return;
                PropertyInfo instanceProperty = AccessTools.Property(dbType, "Instance");
                object db = instanceProperty != null ? instanceProperty.GetValue(null, null) : null;
                if (db == null) return;
                MethodInfo findItem = AccessTools.Method(dbType, "FindItem", new[] { typeof(string) });
                if (findItem == null) return;
                Component original = findItem.Invoke(db, new object[] { "Pulver_Feuerloescher" }) as Component;
                if (original == null) return;
                foreach (var entry in fcRealPolice)
                {
                    Transform car = entry.Root;
                    if (car == null || !car.gameObject.scene.IsValid()) continue;
                    if (car.Find("FC_OriginalExtinguisherStorage") != null) continue;
                    if (fcExtinguisherFailures.Contains(car.GetInstanceID())) continue;
                    Transform decorative = car.Find("Visuals/feuerloescher_pulver_01");
                    if (decorative == null)
                    {
                        // Bei abweichenden Visual-Hierarchien nur einen klaren Treffer verwenden.
                        Transform[] children = car.GetComponentsInChildren<Transform>(true);
                        foreach (Transform t in children)
                            if (t.name == "feuerloescher_pulver_01" && t.GetComponent<Renderer>() != null)
                            { decorative = t; break; }
                    }
                    if (decorative == null) continue;
                    GameObject slot = null;
                    GameObject clone = null;
                    try
                    {
                        slot = new GameObject("FC_OriginalExtinguisherStorage");
                        slot.SetActive(false);
                        slot.transform.SetParent(car, false);
                        slot.transform.position = decorative.position;
                        slot.transform.rotation = decorative.rotation;
                        // Instantiate von inaktiver GameDatabase-Vorlage: Awake erst nach SetParent.
                        clone = UnityEngine.Object.Instantiate(original.gameObject);
                        clone.name = "Pulver_Feuerloescher";
                        clone.SetActive(false);
                        // ORIGINAL-ITEM-LAGERUNG: Item.Awake liest den Parent als Storage,
                        // merkt sich die lokale Pose und stellt den nativen IStorable-Umriss bereit.
                        clone.transform.SetParent(slot.transform, false);
                        clone.transform.localPosition = Vector3.zero;
                        clone.transform.localRotation = Quaternion.identity;
                        clone.transform.localScale = original.transform.localScale;
                        Component item = clone.GetComponent(original.GetType());
                        if (item == null) throw new InvalidOperationException("Original Item-Komponente fehlt");
                        FieldInfo worldField = AccessTools.Field(item.GetType(), "isWorldItem");
                        if (worldField == null) throw new InvalidOperationException("Item.isWorldItem nicht gefunden");
                        worldField.SetValue(item, false);
                        slot.SetActive(true);
                        clone.SetActive(true);
                        PropertyInfo storageProp = AccessTools.Property(item.GetType(), "Storage");
                        Transform storage = storageProp != null ? storageProp.GetValue(item, null) as Transform : null;
                        if (storage != slot.transform)
                            throw new InvalidOperationException("Native Storage-Initialisierung fehlgeschlagen: " + (storage != null ? storage.name : "NULL"));
                        fcExtinguisherWorldItems[car.GetInstanceID()] = clone.transform;
                        { } // logging disabled
                        // Wie ItemHolder.CreateItemInstance(): die native Datenbank-Benachrichtigung
                        // nach Aktivierung senden, damit Spielsysteme die neue Instanz erfassen.
                        // Event via Reflection, um die bestehende FireControl-Referenzliste zu erhalten.
                        FieldInfo spawnedEvent = AccessTools.Field(dbType, "OnObjectInstantiated");
                        if (spawnedEvent != null)
                        {
                            Delegate handler = spawnedEvent.GetValue(null) as Delegate;
                            if (handler != null)
                            {
                                handler.DynamicInvoke(new object[] { clone });
                                { } // logging disabled
                            }
                            else { } // logging disabled
                        }
                        else { } // logging disabled
                        // Erst nach erfolgreicher Original-Item-Initialisierung die Attrappe verbergen.
                        Renderer decorRenderer = decorative.GetComponent<Renderer>();
                        if (decorRenderer != null) decorRenderer.enabled = false;
                        { } // logging disabled
                    }
                    catch (Exception)
                    {
                        fcExtinguisherFailures.Add(car.GetInstanceID());
                        fcExtinguisherWorldItems.Remove(car.GetInstanceID());
                        if (clone != null) UnityEngine.Object.Destroy(clone);
                        if (slot != null) UnityEngine.Object.Destroy(slot);
                        { } // logging disabled
                    }
                }
            }
            catch (Exception)
            {
            }
        }


        // Manuelle Rueckgabe: Die vom Spieler gewaehlte Position und Rotation
        // bleiben erhalten. Kein Snap, kein automatisches Zurueckteleportieren.
        // Innerhalb des Fahrzeugbereichs wird nur die Transform-Elternschaft
        // gesetzt, damit das abgelegte Welt-Item mit dem Fahrzeug mitfaehrt.
        // Beim erneuten Aufnehmen wird die Elternschaft geloest.
        private void FCUpdateExtinguisherWorldAnchors()
        {
            foreach (var entry in fcRealPolice)
            {
                Transform car = entry.Root;
                if (car == null) continue;
                Transform itemTransform;
                if (!fcExtinguisherWorldItems.TryGetValue(car.GetInstanceID(), out itemTransform) || itemTransform == null) continue;
                Transform anchor = car.Find("FC_OriginalExtinguisherStorage");
                if (anchor == null) continue;
                Item item = itemTransform.GetComponent<Item>();
                if (item == null) continue;
                int itemId = item.GetInstanceID();

                if (item.Owner != null)
                {
                    fcExtinguisherWasHeld.Add(itemId);
                    fcExtinguisherReturned.Remove(itemId);
                    fcExtinguisherReturnCandidateSince.Remove(itemId);
                    fcExtinguisherReturnCandidatePos.Remove(itemId);
                    if (itemTransform.parent == car)
                        itemTransform.SetParent(null, true);
                    continue;
                }

                // Vor der allerersten Entnahme liegt der Loescher in seiner
                // Ausgangsposition. Er soll dem Fahrzeug folgen.
                if (!fcExtinguisherWasHeld.Contains(itemId))
                {
                    if (itemTransform.parent != car)
                        itemTransform.SetParent(car, true);
                    continue;
                }

                // Der Spieler hat ihn bereits benutzt. Nur wenn er selbst
                // in den Kofferraumbereich platziert wurde, ans Auto koppeln.
                // Die abgelegte Position/Rotation bleibt UNVERAENDERT.
                float distance = Vector3.Distance(itemTransform.position, anchor.position);
                if (distance <= FC_EXT_RETURN_RADIUS)
                {
                    if (itemTransform.parent != car)
                    {
                        itemTransform.SetParent(car, true);
                        { } // logging disabled
                    }
                    fcExtinguisherReturned.Add(itemId);
                }
                else if (itemTransform.parent == car)
                {
                    // Wurde das Item vom Spiel an einen anderen Ort bewegt,
                    // darf es nicht am Auto haengen bleiben.
                    itemTransform.SetParent(null, true);
                    fcExtinguisherReturned.Remove(itemId);
                }
            }
        }

        // F10: Aufnahmebedingungen der erzeugten Original-Items protokollieren.
        // Reine Diagnose, greift nicht in das Aufheben oder die Tastenbelegung ein.


    }
}
