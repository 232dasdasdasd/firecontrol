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

        private class HallSlot
        {
            [DataMember] public string id = "";
            [DataMember] public string vehicle = "LEER";
            [DataMember] public string anchor = "";
            [DataMember] public float x, y, z;
            [DataMember] public float rotationY;
        }
        [DataContract]
        private class HallConfig
        {
            [DataMember] public bool enabled = true;
            [DataMember] public List<HallSlot> slots = new List<HallSlot>();
        }
        private HallConfig hallConfig = new HallConfig();
        private readonly string[] hallVehicles = {
            "LEER", "LF24", "HLF", "TLF", "TLF3000", "DLK", "KEF",
            "ELW", "RTW", "NEF", "NEF_OLD", "WLF", "PKW", "POL"
        };
        private int hallSelected = 0;
        private Vector2 hallScroll;
        private bool hallPositionDirty;
        private int hallSaveCount;
        private string hallMessage = "Fahrzeug auswählen: FIRE CONTROL spawnt direkt im laufenden Spiel.";
        private static ModMenu? hallInstance;
        private bool hallSpawnExecuted;
        // Kein Sandbox-Status erforderlich.
        private readonly HashSet<string> hallLiveSlots = new HashSet<string>();
        private readonly Dictionary<string, string> hallSpawnNames = new Dictionary<string, string>();
        private uint hallSpawnSerial = 0;
        private bool hallEntityCollisionDetected = false;
        private string HallNewSquad(HallSlot slot)
        {
            // Neue Instanzkennung bei jedem Spawn, auch nach einem Fahrzeugwechsel.
            hallSpawnSerial++;
            return "FC_HALL_" + slot.id + "_" + hallSpawnSerial.ToString("D5");
        }
        private uint HallNewBaseId()
        {
            // Separater hoher ID-Bereich mit Abstand zwischen Fahrzeugen.
            // Wichtig: baseId ist eine numerische Entity-Basis, kein Fahrzeugname.
            return 1000000u + hallSpawnSerial * 1000u;
        }
        private string HallFile => Path.Combine(Paths.ConfigPath, "FireControl", "Fahrzeughallen.json");

        private void HallLog(string msg)
        {
            // Protokollierung deaktiviert.
        }
        private void HallDefault()
        {
            hallConfig = new HallConfig();
            // Nutzerstandard: 11 bisherige Zusatzstellplaetze plus 16 E-Plaetze, ohne C4. Gespeicherte Werte haben Vorrang.
            HallAdd("A1", "PKW_Spawn", "PKW", 7f, 0f, 0f, 0f);
            HallAdd("A2", "PKW_Spawn", "POL", 13f, 0f, 0f, 0f);
            HallAdd("B1", "LF24_Spawn", "HLF", -6f, 0f, 0f, 0f);
            HallAdd("B2", "LF24_Spawn", "TLF3000", -6.59999943f, 0f, -18.60001f, 180f);
            HallAdd("B3", "LF24_Spawn", "LF24", 0f, 0f, -18.50001f, 180f);
            HallAdd("B4", "DLK_Spawn", "DLK", -0.1f, 0f, -16.4000015f, 180f);
            HallAdd("B5", "TLF_Spawn", "TLF", 0f, 0f, -18.9f, 180f);
            HallAdd("C1", "RTW_Spawn", "NEF", 6.39999962f, 0f, -12.5158024f, 180f);
            HallAdd("C2", "RTW_Spawn", "RTW", -6.49999952f, 0f, -12.8000069f, 180f);
            HallAdd("C3", "RTW_Spawn", "RTW", 0f, 0f, -12.9000072f, 180f);
            HallAdd("D1", "WLF_Spawn", "WLF", 12.8558855f, 0f, -0.100000016f, 0f);
            // Halle E: sieben im Spiel eingemessene Stellplaetze (ehemals E2-E8).
            // X/Y/Z sind Feinkorrekturen zur jeweiligen festen Welt-Basisposition.
            HallAdd("E1", "WLF_Spawn", "DLK",     0f,   0f,  0f,   0f);
            HallAdd("E2", "WLF_Spawn", "HLF",     0f,   0f,  0.30f, 0f);
            HallAdd("E3", "WLF_Spawn", "TLF",     0f,   0f,  0f,   0f);
            HallAdd("E4", "WLF_Spawn", "TLF3000", 0.50f,0f, -1.20f,0f);
            HallAdd("E5", "WLF_Spawn", "LF24",    0f,   0f, -1.40f,0f);
            HallAdd("E6", "WLF_Spawn", "KEF",     0f,   0f,  0.60f,0f);
            HallAdd("E7", "WLF_Spawn", "ELW",     0f,   0f, -1.20f,0f);
        }
        // Weltpositionen aus dem Unity-Diagnoselog der alten Static_Vehicles.
        // Die Weltpositionen werden in lokale Offsets von WLF_Spawn umgerechnet.
        // Die Position des Fahrzeug-Pivots kann vom alten statischen Modell abweichen.
        private static bool HallEWorldPosition(string id, out Vector3 position, out float yaw)
        {
            position = Vector3.zero;
            yaw = 0f;
            switch (id)
            {
                // Urspruengliche E2-E8: Basispositionen bleiben identisch.
                case "E1": position = new Vector3(686.24f, 20.10f, -746.78f); yaw = 357.3f; return true;
                case "E2": position = new Vector3(692.66f, 20.10f, -746.86f); yaw = 357.3f; return true;
                case "E3": position = new Vector3(698.98f, 20.10f, -746.19f); yaw = 357.3f; return true;
                case "E4": position = new Vector3(705.01f, 20.10f, -744.99f); yaw = 357.3f; return true;
                case "E5": position = new Vector3(712.02f, 20.10f, -744.20f); yaw = 358.0f; return true;
                case "E6": position = new Vector3(718.90f, 20.10f, -745.78f); yaw = 357.3f; return true;
                case "E7": position = new Vector3(724.86f, 20.10f, -743.34f); yaw = 356.9f; return true;
                default: return false;
            }
        }

        // Halle E nutzt den existierenden Originalanker von D1 (WLF_Spawn).
        // Die Weltkoordinaten der ehemaligen statischen Fahrzeuge werden zur Laufzeit
        // in lokale Offsets relativ zu WLF_Spawn umgerechnet. Die im Menue einstellbaren
        // X/Y/Z-Werte bleiben zusaetzliche Korrekturen in Metern.
        private bool HallTrySpawnOffset(HallSlot slot, out Vector3 offset)
        {
            offset = new Vector3(slot.x, slot.y, slot.z);
            if (slot.id.Length == 0 || slot.id[0] != 'E') return true;
            Transform anchor = GameObject.Find("WLF_Spawn")?.transform;
            if (anchor == null) return false;
            Vector3 target;
            float yaw;
            if (!HallEWorldPosition(slot.id, out target, out yaw)) return false;
            offset += anchor.InverseTransformDirection(target - anchor.position);
            return true;
        }

        private float HallTargetYaw(HallSlot slot, Transform anchor)
        {
            Vector3 target;
            float yaw;
            if (slot.id.Length > 0 && slot.id[0] == 'E' && HallEWorldPosition(slot.id, out target, out yaw))
                return yaw + slot.rotationY;
            return anchor.eulerAngles.y + slot.rotationY;
        }

        private void HallAdd(string id, string anchor, string vehicle, float x, float y, float z, float rotationY)
        {
            hallConfig.slots.Add(new HallSlot { id=id, anchor=anchor, vehicle=vehicle,
                x=x, y=y, z=z, rotationY=rotationY });
        }
        private void HallLoad()
        {
            HallDefault();
            try
            {
                // Bei abgebrochenem Schreibvorgang Sicherung verwenden.
                string source = HallFile;
                if (!File.Exists(source) || new FileInfo(source).Length == 0)
                {
                    if (File.Exists(HallFile + ".bak") && new FileInfo(HallFile + ".bak").Length > 0)
                        source = HallFile + ".bak";
                }
                if (File.Exists(source) && new FileInfo(source).Length > 0)
                {
                    HallConfig? saved;
                    using (var input = File.OpenRead(source))
                        saved = (HallConfig?)new DataContractJsonSerializer(typeof(HallConfig)).ReadObject(input);
                    if (saved != null && saved.slots != null)
                    {
                        hallConfig.enabled = saved.enabled;
                        // Altes Schema eindeutig an E8 erkennen: E2-E8 nach E1-E7 migrieren.
                        bool oldELayout = saved.slots.Exists(x => x != null && x.id == "E8");
                        foreach (HallSlot slot in hallConfig.slots)
                        {
                            string sourceId = slot.id;
                            if (oldELayout && slot.id.StartsWith("E", StringComparison.Ordinal))
                            {
                                int n;
                                if (int.TryParse(slot.id.Substring(1), out n)) sourceId = "E" + (n + 1);
                            }
                            HallSlot? other = saved.slots.Find(x => x != null && x.id == sourceId);
                            if (other == null) continue;
                            if (Array.IndexOf(hallVehicles, other.vehicle) >= 0) slot.vehicle = other.vehicle;
                            slot.x = Mathf.Clamp(other.x, -85f, 85f);
                            slot.y = Mathf.Clamp(other.y, -55f, 55f);
                            slot.z = Mathf.Clamp(other.z, -85f, 85f);
                            slot.rotationY = Mathf.Repeat(other.rotationY, 360f);
                            if (slot.id == "B1" && Mathf.Abs(slot.x - 6f) < 0.001f
                                && Mathf.Abs(slot.y) < 0.001f && Mathf.Abs(slot.z) < 0.001f)
                                slot.x = -6f;
                        }
                    }
                }
                // Legt die Datei auch beim ersten Start an; migriert alte E-Nummern.
                HallSave();
            }
            catch (Exception ex)
            {
                hallMessage = "Ladefehler Fahrzeughallen: " + ex.Message;
                // Fehlerhafte Datei nicht ueberschreiben.
            }
        }
        private void HallSave()
        {
            try
            {
                string? directory = Path.GetDirectoryName(HallFile);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = HallFile + ".tmp";
                // Erst vollstaendig in temporaere Datei schreiben.
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(HallConfig)).WriteObject(output, hallConfig);
                    output.Flush(true);
                }
                // Sicherung und anschliessend Austausch; niemals direkt leeren.
                if (File.Exists(HallFile))
                {
                    try { File.Replace(temp, HallFile, HallFile + ".bak"); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(HallFile, HallFile + ".bak", true);
                        File.Copy(temp, HallFile, true);
                        File.Delete(temp);
                    }
                }
                else File.Move(temp, HallFile);
                hallMessage = "Fahrzeughallen automatisch gespeichert.";
                hallPositionDirty = false;
                hallSaveCount++;
            }
            catch (Exception ex) { hallMessage = "Speicherfehler: " + ex.Message; }
        }
        // Unabhängig von Sandbox.cs: keine Harmony-Änderung an SpawnUnits.
        private void HallInstallPatch()
        {
            hallInstance = this;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += HallSceneLoaded;
            HallLog("Direkter Fahrzeughallen-Modus aktiv (ohne Sandbox-Patch).");
            StartCoroutine(HallSpawnAfterLoad());
        }
        private void HallUnpatch()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= HallSceneLoaded;
            if (hallInstance == this) hallInstance = null;
        }
        private void HallSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            hallLiveSlots.Clear();
            hallSpawnNames.Clear();
            hallSpawnSerial = 0;
            hallSpawnTimes.Clear();
            hallChangeVersions.Clear();
            hallChangeRunning.Clear();
            hallEntityCollisionDetected = false;
            hallSpawnExecuted = false;
            HallLog("Szene geladen: " + scene.name + "; gespeicherte Hallenfahrzeuge werden geprüft.");
            StartCoroutine(HallSpawnAfterLoad());
        }
        private IEnumerator HallSpawnAfterLoad()
        {
            yield return new WaitForSeconds(3f);
            if (!hallConfig.enabled || hallSpawnExecuted || hallEntityCollisionDetected) yield break;
            if (Global.Game == null) { HallLog("Kein Spiel geladen, automatische Hallenspawns verschoben."); yield break; }
            hallSpawnExecuted = true;
            foreach (HallSlot slot in hallConfig.slots)
                if (slot != null && slot.vehicle != "LEER") HallSpawnDirect(slot, false);
        }

        private void HallSpawnFixedOriginals()
        {
            // Unveränderliche Standardfahrzeuge aus der vom Nutzer gelieferten Sandbox.
            // A1/A2, B1-B5, C1-C3 werden NICHT zusätzlich durch das Original belegt.
            // Daher keine POL2/POL3, RTW2/RTW3, HLF, TLF3000, DLK2 im Originalblock.
            string[] types = { "ELW", "LF24", "TLF", "KEF", "DLK", "RTW", "WLF", "NEF", "NEF_OLD", "POL", "PKW" };
            uint[] ids = { 1000u, 2000u, 3000u, 4000u, 5000u, 6000u, 9500u, 9000u, 9200u, 8000u, 7000u };
            string[] anchors = { "ELW_Spawn", "LF24_Spawn", "TLF_Spawn", "KEF_Spawn", "DLK_Spawn", "RTW_Spawn", "WLF_Spawn", "NEF_Spawn", "NEF_OLD_Spawn", "POL_Spawn", "PKW_Spawn" };
            for (int i = 0; i < types.Length; i++)
            {
                try
                {
                    Global.Game.SpawnCar(types[i], types[i], ids[i], anchors[i], Vector3.zero);
                    HallSpawnCrew(types[i], types[i]);
                    HallLog("Originalfahrzeug beibehalten: " + types[i]);
                }
                catch (Exception ex) { HallLog("Originalfahrzeug FEHLER " + types[i] + ": " + ex); }
            }
            // Ursprüngliche Abrollbehälter/Levelobjekte erhalten.
            foreach (string ab in new[] { "AB01", "AB02", "AB10", "AB13", "AB15", "AB16", "AB17", "AB19" })
            {
                try { Global.Game.SpawnLevelObject(ab, ab + "_Spawn"); }
                catch (Exception ex) { HallLog("Original-AB FEHLER " + ab + ": " + ex); }
            }
        }

        private void HallSpawnConfigured()
        {
            // Nur ein Mal pro Sandbox-Instanz. Original-Spawns laufen vorher.
            if (hallSpawnExecuted) { HallLog("SpawnUnits erneut aufgerufen; Duplikate verhindert."); return; }
            hallSpawnExecuted = true;
            if (!hallConfig.enabled) { HallLog("Fahrzeughallen deaktiviert."); return; }
            if (Global.Game == null) { HallLog("FEHLER: Global.Game ist null."); return; }
            int count = 0;
            foreach (HallSlot slot in hallConfig.slots)
            {
                if (slot == null || slot.vehicle == "LEER" || string.IsNullOrEmpty(slot.vehicle)) continue;
                try
                {
                    string squad = HallNewSquad(slot);
                    uint baseId = HallNewBaseId();
                    Vector3 configuredOffset;
                    if (!HallTrySpawnOffset(slot, out configuredOffset)) continue;
                    Global.Game.SpawnCar(squad, slot.vehicle, baseId, slot.anchor,
                        configuredOffset);
                    HallSpawnCrew(squad, slot.vehicle);
                    hallLiveSlots.Add(slot.id);
                    hallSpawnNames[slot.id] = squad;
                    hallSpawnTimes[slot.id] = Time.realtimeSinceStartup;
                    StartCoroutine(HallRotateAfterSpawn(slot, squad));
                    HallLog("OK: " + slot.id + " = " + slot.vehicle + " @ " +
                        slot.anchor + " + (" + slot.x + ", " + slot.y + ", " + slot.z + ")");
                    count++;
                }
                catch (Exception ex) { HallLog("FEHLER bei " + slot.id + ": " + ex); }
            }
            HallLog("Spawnlauf abgeschlossen: " + count + " zusätzliche Fahrzeuge angefordert.");
        }
        // Live-Bearbeitung: entfernt ausschließlich FC_HALL_ Einheiten und setzt
        // sofort ein neues fahrbares Originalfahrzeug mit eigener Kennung ein.
        private static readonly BindingFlags HallFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // IMPORTANT: AIManager owns its own private agentTable, agents and squads.
        // Never edit AIGlobal snapshots or Squad.RemoveAll: AIManager.OnAvatarDestroy
        // must unregister agents from the actual manager collections.
        private bool HallRemoveLiveSlot(HallSlot slot)
        {
            if (!hallSpawnNames.TryGetValue(slot.id, out string squadName))
                return !hallLiveSlots.Contains(slot.id);
            try
            {
                Type? aiType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
                Type? helperType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
                IEnumerable? squads = aiType?.GetProperty("Squads", HallFlags)?.GetValue(null) as IEnumerable;
                if (squads == null || helperType == null)
                    throw new InvalidOperationException("KI-Verwaltung nicht bereit.");
                object? target = null;
                foreach (object? candidate in squads)
                    if (candidate != null && ReadString(candidate, "Name") == squadName)
                    { target = candidate; break; }
                if (target == null)
                    throw new InvalidOperationException("Alte Squad noch nicht sicher auffindbar: " + squadName);
                MethodInfo? getVehicle = null;
                foreach (MethodInfo method in helperType.GetMethods(HallFlags))
                    if (method.Name == "GetVehicle" && method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType.IsInstanceOfType(target))
                    { getVehicle = method; break; }
                object? car = getVehicle?.Invoke(null, new object[] { target });
                if (car is not Component vehicle || vehicle == null)
                    throw new InvalidOperationException("Fahrzeug nicht gefunden: " + squadName);
                var avatars = new List<GameObject>();
                IEnumerable? agents = ReadProperty(target, "Agents") as IEnumerable;
                if (agents != null)
                    foreach (object? agent in agents)
                    {
                        object? avatar = ReadProperty(agent, "Avatar");
                        if (avatar is Component c && c != null) avatars.Add(c.gameObject);
                    }
                foreach (GameObject avatar in avatars) HallCheckDespawn(avatar);
                HallCheckDespawn(vehicle.gameObject);
                // Native Avatar.OnAvatarDestroy -> AIManager.OnAvatarDestroy removes
                // from its PRIVATE lists and agentTable, then from Squad and AIGlobal.
                foreach (GameObject avatar in avatars)
                    if (avatar != null) HallDespawnObject(avatar);
                if (vehicle != null) HallDespawnObject(vehicle.gameObject);
                hallLiveSlots.Remove(slot.id);
                hallSpawnNames.Remove(slot.id);
                hallSpawnTimes.Remove(slot.id);
                HallLog("Native Despawns angefordert: " + squadName + " / " + avatars.Count + " Avatare.");
                return true;
            }
            catch (Exception ex)
            {
                HallLog("Austausch abgebrochen " + slot.id + ": " + ex);
                hallMessage = "Fahrzeugwechsel blockiert: " + ex.Message;
                return false;
            }
        }

        private static void HallCheckDespawn(GameObject obj)
        {
            Component? network = null;
            foreach (Component c in obj.GetComponents<Component>())
                if (c != null && c.GetType().Name == "NetworkObject") { network = c; break; }
            if (network == null || network.GetType().GetMethod("Despawn", new[] { typeof(bool) }) == null)
                throw new InvalidOperationException("Netcode-Despawn nicht verfügbar: " + obj.name);
        }

        private bool HallOldSquadGone(string squadName)
        {
            try
            {
                Type? aiType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
                IEnumerable? squads = aiType?.GetProperty("Squads", HallFlags)?.GetValue(null) as IEnumerable;
                if (squads == null) return false;
                foreach (object? squad in squads)
                    if (squad != null && ReadString(squad, "Name") == squadName) return false;
                return true;
            }
            catch { return false; }
        }

        private void HallClearSelectionIndexes()
        {
            Type? displayType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.UI.UnitDisplay");
            if (displayType == null) return;
            foreach (string name in new[] { "Displays", "DisplayIndex" })
                if (displayType.GetField(name, HallFlags)?.GetValue(null) is IDictionary dictionary)
                    dictionary.Clear();
        }

        private void HallDespawnObject(GameObject obj)
        {
            // Native Netcode-Entfernung, sofern eine NetworkObject-Komponente existiert.
            Component? net = null;
            foreach (Component c in obj.GetComponents<Component>())
                if (c != null && c.GetType().Name == "NetworkObject") { net = c; break; }
            if (net != null)
            {
                MethodInfo? despawn = net.GetType().GetMethod("Despawn", new Type[] { typeof(bool) });
                if (despawn != null) { despawn.Invoke(net, new object[] { true }); return; }
            }
            // Keine ungesicherte Destroy-Operation für vernetzte Spielobjekte.
            throw new InvalidOperationException("Netzwerk-Despawn für " + obj.name + " nicht verfügbar.");
        }

        private bool HallRotateLiveSlot(HallSlot slot)
        {
            if (slot.id.Length > 0 && slot.id[0] == 'E' && GameObject.Find("WLF_Spawn") == null) return false;
            if (!hallSpawnNames.TryGetValue(slot.id, out string squadName)) return false;
            try
            {
                Type? aiType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
                Type? helperType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
                IEnumerable? squads = aiType?.GetProperty("Squads", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable;
                if (squads == null || helperType == null) return false;
                foreach (object? squad in squads)
                {
                    if (squad == null || ReadString(squad, "Name") != squadName) continue;
                    foreach (MethodInfo method in helperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (method.Name != "GetVehicle" || method.GetParameters().Length != 1 ||
                            !method.GetParameters()[0].ParameterType.IsInstanceOfType(squad)) continue;
                        object? vehicle = method.Invoke(null, new object[] { squad });
                        if (vehicle is Component component && component != null)
                        {
                            // Relative Drehung zum Spawnanker statt absolute Weltrotation.
                            Transform? anchor = GameObject.Find(slot.anchor)?.transform;
                            float yaw = anchor != null ? HallTargetYaw(slot, anchor) : slot.rotationY;
                            component.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { HallLog("Drehung fehlgeschlagen bei " + slot.id + ": " + ex); }
            return false;
        }
        private IEnumerator HallRotateAfterSpawn(HallSlot slot, string squadName)
        {
            // Unity/Netcode kann die Rotation nach dem ersten Spawn-Frame erneut setzen.
            // Darum die gespeicherte Ausrichtung nur waehrend der Startphase stabilisieren.
            // Danach keinerlei dauerhafte Update-Schleife / FPS-Belastung.
            bool rotated = false;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                if (!hallSpawnNames.TryGetValue(slot.id, out string current) || current != squadName)
                    yield break;
                if (HallRotateLiveSlot(slot)) rotated = true;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            if (!rotated)
                HallLog("Drehung " + slot.id + ": Fahrzeug-Transform in der Startphase nicht gefunden.");
        }
        private readonly Dictionary<string, int> hallChangeVersions = new Dictionary<string, int>();
        private readonly HashSet<string> hallChangeRunning = new HashSet<string>();
        private readonly Dictionary<string, float> hallSpawnTimes = new Dictionary<string, float>();

        private void HallApplyLiveSlot(HallSlot slot)
        {
            HallSave();
            int version;
            hallChangeVersions.TryGetValue(slot.id, out version);
            hallChangeVersions[slot.id] = version + 1;
            if (!hallChangeRunning.Contains(slot.id))
                StartCoroutine(HallReplaceSlotSafely(slot));
        }
        private IEnumerator HallReplaceSlotSafely(HallSlot slot)
        {
            hallChangeRunning.Add(slot.id);
            try
            {
                while (true)
                {
                    int version = hallChangeVersions[slot.id];
                    // Charakter-Skins dürfen fertig initialisieren, bevor Avatare entfernt werden.
                    if (hallLiveSlots.Contains(slot.id) && hallSpawnTimes.TryGetValue(slot.id, out float born))
                    {
                        float remaining = 2.5f - (Time.realtimeSinceStartup - born);
                        if (remaining > 0f) yield return new WaitForSecondsRealtime(remaining);
                    }
                    if (hallLiveSlots.Contains(slot.id))
                    {
                        string oldSquad = hallSpawnNames[slot.id];
                        if (!HallRemoveLiveSlot(slot))
                        {
                            hallMessage = "Wechsel auf " + slot.id + " gestoppt: vorheriges Fahrzeug nicht sicher entfernt.";
                            yield break;
                        }
                        // Wait for AIManager.OnAvatarDestroy to unregister the PRIVATE agent list.
                        float deadline = Time.realtimeSinceStartup + 8f;
                        while (!HallOldSquadGone(oldSquad) && Time.realtimeSinceStartup < deadline)
                            yield return new WaitForSecondsRealtime(0.1f);
                        if (!HallOldSquadGone(oldSquad))
                        {
                            hallMessage = "Wechsel blockiert: KI hat alte Squad nicht abgemeldet.";
                            HallLog(hallMessage + " " + oldSquad);
                            yield break;
                        }
                        HallClearSelectionIndexes();
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                    // Nur die zuletzt ausgewählte Variante spawnen.
                    if (version != hallChangeVersions[slot.id]) continue;
                    if (slot.vehicle != "LEER")
                    {
                        HallSpawnDirect(slot, false);
                        if (hallLiveSlots.Contains(slot.id))
                            hallSpawnTimes[slot.id] = Time.realtimeSinceStartup;
                    }
                    if (version == hallChangeVersions[slot.id]) break;
                }
            }
            finally { hallChangeRunning.Remove(slot.id); }
        }

        private bool HallMoveLiveSlot(HallSlot slot)
        {
            if (slot.id.Length > 0 && slot.id[0] == 'E' && GameObject.Find("WLF_Spawn") == null) return false;
            if (!hallSpawnNames.TryGetValue(slot.id, out string squadName)) return false;
            try
            {
                Type? aiType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
                Type? helperType = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
                IEnumerable? squads = aiType?.GetProperty("Squads", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IEnumerable;
                if (squads == null || helperType == null) return false;
                foreach (object? squad in squads)
                {
                    if (squad == null || ReadString(squad, "Name") != squadName) continue;
                    foreach (MethodInfo method in helperType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (method.Name != "GetVehicle" || method.GetParameters().Length != 1 ||
                            !method.GetParameters()[0].ParameterType.IsInstanceOfType(squad)) continue;
                        object? vehicle = method.Invoke(null, new object[] { squad });
                        if (vehicle is Component component && component != null)
                        {
                            Transform? anchor = GameObject.Find(slot.anchor)?.transform;
                            if (anchor == null) { HallLog("Positionsanker fehlt: " + slot.anchor); return false; }
                            // Derselbe Offset wie bei SpawnCar, nur ohne neues Fahrzeug/Besatzung.
                            Vector3 offset;
                            if (!HallTrySpawnOffset(slot, out offset)) return false;
                            component.transform.position = anchor.position + anchor.TransformDirection(offset);
                            component.transform.rotation = Quaternion.Euler(0f, HallTargetYaw(slot, anchor), 0f);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex) { HallLog("Live-Position " + slot.id + ": " + ex); }
            return false;
        }

        private void HallSpawnDirect(HallSlot slot, bool replace)
        {
            Vector3 spawnOffset;
            if (!HallTrySpawnOffset(slot, out spawnOffset))
            {
                hallMessage = "WLF_Spawn ist noch nicht bereit fuer " + slot.id + ".";
                return;
            }
            if (!hallConfig.enabled) { hallMessage = "Hallenmanager deaktiviert."; return; }
            if (hallEntityCollisionDetected)
            {
                hallMessage = "Entity-ID-Kollision erkannt. Keine weiteren Spawns bis zum Szenenwechsel.";
                return;
            }
            if (Global.Game == null)
            {
                hallMessage = "Spielwelt noch nicht bereit. Konfiguration gespeichert.";
                HallLog("Spawn " + slot.id + " wartet: Global.Game fehlt.");
                return;
            }
            if (replace && !HallRemoveLiveSlot(slot))
            {
                hallMessage = "Austausch gestoppt: altes Fahrzeug konnte nicht sicher entfernt werden.";
                HallLog("Spawn " + slot.id + " gestoppt, um Doppelbelegung zu verhindern.");
                return;
            }
            if (slot.vehicle == "LEER") { hallMessage = slot.id + " geleert."; return; }
            if (hallLiveSlots.Contains(slot.id))
            {
                HallLog("Doppelspawn verhindert: " + slot.id);
                return;
            }
            try
            {
                string squad = HallNewSquad(slot);
                uint baseId = HallNewBaseId();
                HallLog("SpawnCar: " + squad + " / " + slot.vehicle + " / " + slot.anchor + " / baseId=" + baseId);
                Global.Game.SpawnCar(squad, slot.vehicle, baseId, slot.anchor,
                    spawnOffset);
                hallLiveSlots.Add(slot.id);
                hallSpawnNames[slot.id] = squad;
                hallSpawnTimes[slot.id] = Time.realtimeSinceStartup;
                StartCoroutine(HallRotateAfterSpawn(slot, squad));
                try { HallSpawnCrew(squad, slot.vehicle); }
                catch (Exception crewEx) { HallLog("Fahrzeug gespawnt, Besatzung fehlgeschlagen: " + crewEx); }
                hallMessage = "Spawn-Aufruf ausgeführt (Erfolg im Spiel prüfen): " + slot.vehicle + " auf " + slot.id;
                HallLog(hallMessage + " Offset " + slot.x + "," + slot.y + "," + slot.z);
            }
            catch (Exception ex)
            {
                hallMessage = "Spawn fehlgeschlagen: " + ex.Message;
                HallLog("FEHLER " + slot.id + ": " + ex);
            }
        }

        private void HallSpawnCrew(string squad, string type)
        {
            // Besatzungen aus dem vom Nutzer bereitgestellten Sandbox.SpawnUnits.
            switch (type)
            {
                case "LF24":
                    Global.Game.SpawnAgent(squad, "SquadLeader", "fireman_lf24_leader", "Leader", "Gruppenfuehrer", "LF24_Leader", new Vector3(0,0,0));
                    Global.Game.SpawnAgent(squad, "ATLeader", "fireman_lf24_attacktroop_leader", "AttackLeader", "Angriffstruppfuehrer", "LF24_Troop", new Vector3(1,0,0));
                    Global.Game.SpawnAgent(squad, "ATTroop", "fireman_lf24_attacktroop", "AttackTroop", "Angriffstruppmann", "LF24_Troop", new Vector3(2,0,0));
                    Global.Game.SpawnAgent(squad, "WTLeader", "fireman_lf24_watertroop_leader", "DefaultAgentSetup", "Wassertruppfuehrer", "LF24_Troop", new Vector3(3,0,0));
                    Global.Game.SpawnAgent(squad, "WTTroop", "fireman_lf24_watertroop", "WaterTroop", "Wassertruppmann", "LF24_Troop", new Vector3(4,0,0));
                    Global.Game.SpawnAgent(squad, "LF24Engineer", "fireman_lf24_engineer", "Engineer", "Maschinist", "LF24_Troop", new Vector3(5,0,0));
                    break;
                case "HLF": case "TLF3000":
                    Global.Game.SpawnAgent(squad, "TLFEngineer", "fireman_tlf_engineer", "TLFEngineer", "Maschinist", "TLF_Troop", Vector3.zero); break;
                case "TLF":
                    Global.Game.SpawnAgent(squad, "TLFLeader", "fireman_tlf_leader", "TLFAttackTroop", "Angriffstruppfuehrer", "TLF_Troop", new Vector3(1,0,0));
                    Global.Game.SpawnAgent(squad, "TLFEngineer", "fireman_tlf_engineer", "TLFEngineer", "Maschinist", "TLF_Troop", Vector3.zero); break;
                case "KEF":
                    Global.Game.SpawnAgent(squad, "KEFLeader", "fireman_kef_leader", "KEFLeader", "Angriffstruppfuehrer", "KEF_Troop", new Vector3(1,0,0));
                    Global.Game.SpawnAgent(squad, "KEFEngineer", "fireman_kef_engineer", "KEFEngineer", "Maschinist", "KEF_Troop", Vector3.zero); break;
                case "DLK":
                    Global.Game.SpawnAgent(squad, "DLKLeader", "fireman_dlk_leader", "DLKLeader", "Angriffstruppfuehrer", "DLK_Troop", new Vector3(1,0,0));
                    Global.Game.SpawnAgent(squad, "DLKEngineer", "fireman_dlk_engineer", "DLKEngineer", "Maschinist", "DLK_Troop", Vector3.zero); break;
                case "ELW":
                    Global.Game.SpawnAgent(squad, "IncidentCmdr", "fireman_elw_commander", "Leader", "Einsatzleiter", "ELW_Troop", Vector3.zero);
                    Global.Game.SpawnAgent(squad, "Messenger", "fireman_elw_assistant", "Messenger", "Fuehrungsassistent", "ELW_Troop", new Vector3(1,0,0)); break;
                case "RTW":
                    Global.Game.SpawnAgent(squad, "Paramedic", "fireman_rtw_officer", "Paramedic", "Notfallsanitaeter", "RTW_Troop", Vector3.zero);
                    Global.Game.SpawnAgent(squad, "RTWEngineer", "fireman_rtw_paramedic", "RTWEngineer", "Rettungssanitaeter", "RTW_Troop", new Vector3(1,0,0)); break;
                case "NEF": case "NEF_OLD":
                    Global.Game.SpawnAgent(squad, "NEFLeader", "fireman_nef_leader", "NEFLeader", "Notarzt", "NEF_Troop", Vector3.zero); break;
                case "WLF":
                    Global.Game.SpawnAgent(squad, "WLFLeader", "fireman_wlf_leader", "WLFLeader", "Truppfuehrer", "WLF_Troop", new Vector3(1,0,0));
                    Global.Game.SpawnAgent(squad, "WLFEngineer", "fireman_wlf_engineer", "WLFEngineer", "Maschinist", "WLF_Troop", Vector3.zero); break;
                case "POL":
                    Global.Game.SpawnAgent(squad, "Policeman", "police_default", "Police", "Polizist", "WLF_Troop", Vector3.zero); break;
            }
        }
        private void HallDraw()
        {
            GUILayout.Label("FAHRZEUGHALLEN | EIGENE ZUSATZFAHRZEUGE", headingStyle);
            GUILayout.Label("Halle wählen, Fahrzeug aussuchen, Position feinjustieren. " +
                "Feste Originalfahrzeuge bleiben gesperrt. Auswahl spawnt DIREKT im laufenden Spiel.", infoStyle);
            GUILayout.BeginVertical(panelStyle);
            bool active = GUILayout.Toggle(hallConfig.enabled, "Zusätzliche Hallenfahrzeuge aktivieren");
            if (active != hallConfig.enabled) { hallConfig.enabled = active; HallSave(); }
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 5; i++)
                if (GUILayout.Button("HALLE " + (char)('A' + i), hallSelected == i ? activeButtonStyle : buttonStyle))
                    hallSelected = i;
            GUILayout.EndHorizontal();
            if (hallSelected == 0) GUILayout.Label("GESPERRT (Original): KdoW, ELW", infoStyle);
            if (hallSelected == 1) GUILayout.Label("GESPERRT (Original): KEF, TLF, DLK, LF24", infoStyle);
            if (hallSelected == 2) GUILayout.Label("GESPERRT (Original): RTW, NEF", infoStyle);
            if (hallSelected == 3) GUILayout.Label("WLF_Spawn: Original-WLF bleibt unverändert. D1 ist ein zusätzlicher WLF-Platz.", infoStyle);
            if (hallSelected == 4) GUILayout.Label("HALLE E: 7 eingemessene Stellplaetze E1-E7. Fahrzeuge und Positionen werden automatisch gespeichert.", infoStyle);
            GUILayout.Label("Bearbeitbar: A1/A2, B1-B5, C1-C3, D1 und E1-E7.", infoStyle);
            GUILayout.EndVertical();

            hallScroll = GUILayout.BeginScrollView(hallScroll, GUILayout.Height(Mathf.Max(170f, windowRect.height - 390f)));
            foreach (HallSlot slot in hallConfig.slots)
            {
                if (slot.id[0] != (char)('A' + hallSelected)) continue;
                GUILayout.BeginVertical(panelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("PLATZ " + slot.id + "  |  " + slot.vehicle, headingStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("LEEREN", smallButtonStyle)) { slot.vehicle = "LEER"; HallApplyLiveSlot(slot); }
                if (GUILayout.Button("POSITION ZURUECKSETZEN", smallButtonStyle))
                {
                    string anchor = slot.anchor;
                    if (slot.id == "A1") { slot.x=7f; slot.y=0f; slot.z=0f; slot.rotationY=0f; }
                    else if (slot.id == "A2") { slot.x=13f; slot.y=0f; slot.z=0f; slot.rotationY=0f; }
                    else if (slot.id == "B1") { slot.x=-6f; slot.y=0f; slot.z=0f; slot.rotationY=0f; }
                    else if (slot.id == "B2") { slot.x=-6.59999943f; slot.y=0f; slot.z=-18.60001f; slot.rotationY=180f; }
                    else if (slot.id == "B3") { slot.x=0f; slot.y=0f; slot.z=-18.50001f; slot.rotationY=180f; }
                    else if (slot.id == "B4") { slot.x=-0.1f; slot.y=0f; slot.z=-16.4000015f; slot.rotationY=180f; }
                    else if (slot.id == "B5") { slot.x=0f; slot.y=0f; slot.z=-18.9f; slot.rotationY=180f; }
                    else if (slot.id == "C1") { slot.x=6.39999962f; slot.y=0f; slot.z=-12.5158024f; slot.rotationY=180f; }
                    else if (slot.id == "C2") { slot.x=-6.49999952f; slot.y=0f; slot.z=-12.8000069f; slot.rotationY=180f; }
                    else if (slot.id == "C3") { slot.x=0f; slot.y=0f; slot.z=-12.9000072f; slot.rotationY=180f; }
                    else if (slot.id == "D1") { slot.x=12.8558855f; slot.y=0f; slot.z=-0.100000016f; slot.rotationY=0f; }
                    else if (slot.id[0] == 'E')
                    {
                        // Standardposition aus HallDefault direkt und ohne Seiteneffekte ablesen.
                        switch (slot.id)
                        {
                            case "E1": slot.x=0f; slot.y=0f; slot.z=0f; break;
                            case "E2": slot.x=0f; slot.y=0f; slot.z=0.30f; break;
                            case "E3": slot.x=0f; slot.y=0f; slot.z=0f; break;
                            case "E4": slot.x=0.50f; slot.y=0f; slot.z=-1.20f; break;
                            case "E5": slot.x=0f; slot.y=0f; slot.z=-1.40f; break;
                            case "E6": slot.x=0f; slot.y=0f; slot.z=0.60f; break;
                            case "E7": slot.x=0f; slot.y=0f; slot.z=-1.20f; break;
                        }
                        slot.rotationY=0f;
                    }
                    HallSave();
                    HallMoveLiveSlot(slot);
                    HallRotateLiveSlot(slot);
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Fahrzeug wählen:", infoStyle);
                GUILayout.BeginHorizontal();
                for (int i = 0; i < hallVehicles.Length; i++)
                {
                    if (i == 7) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                    string vehicle = hallVehicles[i];
                    if (GUILayout.Button(vehicle, slot.vehicle == vehicle ? activeButtonStyle : smallButtonStyle))
                    { if (slot.vehicle != vehicle) { slot.vehicle = vehicle; HallApplyLiveSlot(slot); } }
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Spawnanker: " + slot.anchor + (slot.id[0] == 'E' ? " | automatische Basisposition + Feinkorrektur" : " (gesperrt)"), infoStyle);
                GUILayout.Label("X: " + slot.x.ToString("F2") + " m", infoStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-0.10", smallButtonStyle)) { slot.x -= 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                float nextX = GUILayout.HorizontalSlider(slot.x, -85f, 85f);
                if (Mathf.Abs(nextX - slot.x) > 0.001f) { slot.x = nextX; HallSave(); HallMoveLiveSlot(slot); }
                if (GUILayout.Button("+0.10", smallButtonStyle)) { slot.x += 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                GUILayout.EndHorizontal();
                GUILayout.Label("Y (Höhe): " + slot.y.ToString("F2") + " m", infoStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-0.10", smallButtonStyle)) { slot.y -= 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                float nextY = GUILayout.HorizontalSlider(slot.y, -55f, 55f);
                if (Mathf.Abs(nextY - slot.y) > 0.001f) { slot.y = nextY; HallSave(); HallMoveLiveSlot(slot); }
                if (GUILayout.Button("+0.10", smallButtonStyle)) { slot.y += 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                GUILayout.EndHorizontal();
                GUILayout.Label("Z: " + slot.z.ToString("F2") + " m", infoStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-0.10", smallButtonStyle)) { slot.z -= 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                float nextZ = GUILayout.HorizontalSlider(slot.z, -85f, 85f);
                if (Mathf.Abs(nextZ - slot.z) > 0.001f) { slot.z = nextZ; HallSave(); HallMoveLiveSlot(slot); }
                if (GUILayout.Button("+0.10", smallButtonStyle)) { slot.z += 0.10f; HallSave(); HallMoveLiveSlot(slot); }
                GUILayout.EndHorizontal();
                GUILayout.Label("Drehung: " + slot.rotationY.ToString("F0") + "° (0–360°)", infoStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("-10°", smallButtonStyle))
                { slot.rotationY = Mathf.Repeat(slot.rotationY - 10f, 360f); HallSave(); HallRotateLiveSlot(slot); }
                float nextRotation = GUILayout.HorizontalSlider(slot.rotationY, 0f, 360f);
                if (Mathf.Abs(nextRotation - slot.rotationY) > 0.01f)
                { slot.rotationY = nextRotation; HallSave(); HallRotateLiveSlot(slot); }
                if (GUILayout.Button("+10°", smallButtonStyle))
                { slot.rotationY = Mathf.Repeat(slot.rotationY + 10f, 360f); HallSave(); HallRotateLiveSlot(slot); }
                if (GUILayout.Button("180°", smallButtonStyle))
                { slot.rotationY = Mathf.Repeat(slot.rotationY + 180f, 360f); HallSave(); HallRotateLiveSlot(slot); }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            if (hallPositionDirty && Event.current != null && Event.current.rawType == EventType.MouseUp)
            {
                HallSave();
                foreach (HallSlot activeSlot in hallConfig.slots)
                    if (activeSlot.id[0] == (char)('A' + hallSelected)) HallMoveLiveSlot(activeSlot);
                hallPositionDirty = false;
                hallMessage = "Position live übernommen und gespeichert.";
            }
            GUILayout.BeginVertical(panelStyle);
            if (GUILayout.Button("FAHRZEUGE NEU SPAWNEN (FALLS NOETIG)", activeButtonStyle))
            {
                HallSave();
                foreach (HallSlot slot in hallConfig.slots)
                    if (slot.id[0] == (char)('A' + hallSelected)) HallApplyLiveSlot(slot);
            }
            if (GUILayout.Button("ALLE EINSTELLUNGEN SPEICHERN", buttonStyle)) { HallSave(); SaveAllSettings(true); }
            GUILayout.Label(hallMessage, infoStyle);
            GUILayout.Label("FIRE CONTROL ruft SpawnCar direkt auf, ohne Sandbox.cs zu verändern. " +
                "Feste Originalfahrzeuge bleiben erhalten; A1-A2, B1-B5, C1-C3, D1 und E1-E7 werden sofort geaendert und dauerhaft gespeichert. " +
                "Vollständige Fahrzeugfunktionen und exakte Positionen müssen im Spiel geprüft werden.", infoStyle);
            GUILayout.EndVertical();
        }

    }
}
