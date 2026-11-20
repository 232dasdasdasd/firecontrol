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

        private void DrawOverview()
        {
            // Gesamtes Dashboard scrollbar: Nachalarmierung bleibt auch bei vielen
            // alarmierten Fahrzeugen auf kleinen Auflösungen erreichbar.
            dashboardScroll = GUILayout.BeginScrollView(dashboardScroll,
                false, true, GUILayout.ExpandHeight(true));
            GUILayout.Label("DASHBOARD", headingStyle);
            GUILayout.Space(15);

            EnviromentHandler environment = GetEnvironment();
            SituationController controller = GetSituationController();

            GUILayout.BeginVertical(panelStyle);

            GUILayout.Label("AKTUELLE SPIELZEIT", textStyle);

            if (environment != null)
            {
                GUILayout.Label(
                    environment.DateTime.ToString("HH:mm:ss"),
                    titleStyle);

                GUILayout.Label(
                    environment.DateTime.ToString("dd.MM.yyyy"),
                    textStyle);
            }
            else
            {
                GUILayout.Label("Keine aktive Spielwelt", textStyle);
            }

            GUILayout.EndVertical();
            GUILayout.Space(15);

            GUILayout.BeginVertical(panelStyle);

            GUILayout.Label("EINSATZSTATUS", headingStyle);

            GUILayout.Label(
                controller == null
                    ? "Einsatzcontroller nicht gefunden"
                    : controller.IsSituationActive
                        ? "Ein Einsatz ist aktiv oder wird vorbereitet"
                        : "Kein aktiver Einsatz",
                textStyle);

            if (GUILayout.Button(
                "EINSATZLEITSTELLE ÖFFNEN",
                activeButtonStyle))
            {
                selectedTab = 1;
            }

            GUILayout.EndVertical();
            GUILayout.Space(12);
            DrawSupportDashboard(controller);
            GUILayout.Space(18);
            GUILayout.EndScrollView();
        }

        // Nachalarmierung über die originale ISquad-Schnittstelle des Spiels.
        // Die Spiel-KI übernimmt Ausrücken und Fahrt; es wird nichts teleportiert.
        private List<object> GetLiveSquads()
        {
            List<object> result = new List<object>();
            Type? ai = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
            if (ai == null) return result;
            const BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            object? value = ai.GetProperty("Squads", f)?.GetValue(null)
                ?? ai.GetField("Squads", f)?.GetValue(null);
            if (value is IEnumerable enumerable)
                foreach (object? item in enumerable)
                    if (item != null) result.Add(item);
            return result;
        }

        private object? GetCurrentSituation(List<object> squads)
        {
            foreach (object squad in squads)
            {
                object? situation = ReadProperty(squad, "Situation");
                if (situation != null) return situation;
            }
            return null;
        }

        private void DrawSupportDashboard(SituationController? controller)
        {
            GUILayout.BeginVertical(panelStyle);
            GUILayout.Label("NACHALARMIERUNG", headingStyle);
            if (controller == null || !controller.IsSituationActive)
            {
                supportSelected.Clear();
                GUILayout.Label("Kein laufender Einsatz – Nachalarmierung derzeit nicht verfügbar.", infoStyle);
                GUILayout.EndVertical();
                return;
            }
            GUILayout.Label("EINSATZ LÄUFT – zusätzliche Einheiten auswählen", textStyle);
            if (!controller.IsServer)
            {
                GUILayout.Label("Nachalarmierung nur auf dem Spielserver möglich.", infoStyle);
                GUILayout.EndVertical();
                return;
            }

            List<object> squads;
            object? situation;
            try
            {
                squads = GetLiveSquads();
                situation = GetCurrentSituation(squads);
            }
            catch (Exception ex)
            {
                GUILayout.Label("Einheiten konnten nicht gelesen werden: " + ex.Message, infoStyle);
                GUILayout.EndVertical();
                return;
            }
            if (situation == null)
            {
                GUILayout.Label("Einsatz aktiv, aber noch keiner Einheit zugewiesen. Bitte warten, bis die Originalalarmierung abgeschlossen ist.", infoStyle);
                GUILayout.EndVertical();
                return;
            }
            DrawDeployedSquads(squads, situation);
            GUILayout.Space(12);
            GUILayout.Label("Verfügbare Fahrzeuge (einzeln auswählen):", infoStyle);
            int freeCount = 0;
            supportScroll = GUILayout.BeginScrollView(supportScroll, GUILayout.Height(180));
            foreach (object squad in squads)
            {
                if (ReadProperty(squad, "Situation") != null) continue;
                string status = ReadString(squad, "Status");
                if (status != "DutyHome") continue;
                object? allowed = ReadProperty(squad, "IsAllowedForCurrentUser");
                if (allowed is bool b && !b) continue;
                string name = ReadString(squad, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Nur Einheiten mit einem tatsächlich zugeordneten Fahrzeug anzeigen.
                Type? wm = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
                MethodInfo? getVehicle = null;
                if (wm != null)
                    foreach (MethodInfo method in wm.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        if (method.Name == "GetVehicle" && method.GetParameters().Length == 1 &&
                            method.GetParameters()[0].ParameterType.IsInstanceOfType(squad)) { getVehicle = method; break; }
                if (getVehicle == null) continue;
                object? vehicle;
                try { vehicle = getVehicle.Invoke(null, new object[] { squad }); }
                catch { continue; }
                if (vehicle == null) continue;
                freeCount++;
                bool selected = supportSelected.Contains(name);
                bool newSelected = GUILayout.Toggle(selected, name + "  |  " +
                    ((vehicle as UnityEngine.Object)?.name ?? "Fahrzeug"), buttonStyle);
                if (newSelected) supportSelected.Add(name);
                else supportSelected.Remove(name);
            }
            GUILayout.EndScrollView();
            if (freeCount == 0) GUILayout.Label("Keine freien Fahrzeuge gefunden.", infoStyle);
            GUILayout.Label(supportSelected.Count + " Einheit(en) ausgewählt", infoStyle);
            GUI.enabled = supportSelected.Count > 0;
            if (GUILayout.Button("AUSGEWÄHLTE EINHEITEN NACHALARMIEREN", activeButtonStyle))
                DispatchSupport(controller, squads, situation);
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        // Übersicht: nur Live-Daten der originalen Squad-/Fahrzeug-KI.
        // Keine Statusänderungen und keine zusätzlichen Fahrbefehle.
        private void DrawDeployedSquads(List<object> squads, object situation)
        {
            GUILayout.Label("ALARMIERTE FAHRZEUGE / LIVE-STATUS", headingStyle);
            GUILayout.Label("Originalbilder und taktische Symbole, soweit vom Spiel geladen.", infoStyle);
            int count = 0;
            deployedScroll = GUILayout.BeginScrollView(deployedScroll, GUILayout.Height(225));
            foreach (object squad in squads)
            {
                if (!ReferenceEquals(ReadProperty(squad, "Situation"), situation)) continue;
                count++;
                string name = ReadString(squad, "Name");
                string raw = ReadString(squad, "Status");
                VehicleEntry? entry = vehicles.Find(v => v.SquadName == name);
                string vehicleName = entry?.VehicleName ?? name;
                string type = entry?.VehicleType ?? DetectVehicleType(vehicleName + " " + name);
                string displayStatus = raw;
                Color color = new Color(1f, .77f, .24f);
                if (raw == "EmergencyCall") { displayStatus = "ALARM / BESATZUNG RÜCKT AUS"; color = new Color(1f, .67f, .22f); }
                else if (raw.IndexOf("Arrival", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         raw.IndexOf("Situation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         raw.IndexOf("Scene", StringComparison.OrdinalIgnoreCase) >= 0)
                { displayStatus = "AM EINSATZ / " + raw; color = new Color(.33f, .9f, .48f); }
                else if (raw.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         raw.IndexOf("Route", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         raw.IndexOf("Way", StringComparison.OrdinalIgnoreCase) >= 0)
                { displayStatus = "UNTERWEGS / " + raw; color = new Color(.3f, .75f, 1f); }
                object? car = FindSquadVehicle(squad);
                object? driver = car is Component c ? c.GetComponent("AIDriver") : null;
                object? info = ReadProperty(driver, "Info");
                bool driving = ReadProperty(info, "Drive") is bool drive && drive;
                bool playerControlled = ReadProperty(info, "PlayerControlled") is bool pc && pc;
                bool stopped = ReadProperty(car, "HasStopped") is bool stop && stop;
                string parking = ReadProperty(ReadProperty(info, "From"), "parkingSpotType")?.ToString() ?? "";
                if (driving && !playerControlled && parking != "Mission")
                { displayStatus = "UNTERWEGS (KI FÄHRT)"; color = new Color(.3f, .75f, 1f); }
                else if (stopped && parking == "Mission")
                { displayStatus = "AM EINSATZORT (FAHRZEUG)"; color = new Color(.33f, .9f, .48f); }
                else if (raw == "EmergencyCall" && !driving)
                { displayStatus = "ALARMIERT / AUSFAHRT NOCH NICHT BESTÄTIGT"; }
                GUILayout.BeginHorizontal(panelStyle);
                if (entry != null) DrawVehiclePicture(entry);
                else GUILayout.Label(type, imageBadgeStyle, GUILayout.Width(192), GUILayout.Height(110));
                GUILayout.BeginVertical();
                GUILayout.Label(vehicleName + "  |  " + type, headingStyle);
                GUILayout.Label("EINHEIT: " + name, infoStyle);
                Color old = GUI.contentColor;
                GUI.contentColor = color;
                GUILayout.Label("●  " + displayStatus, textStyle);
                GUI.contentColor = old;
                if (entry != null)
                {
                    Sprite? sign = GetMapVehicleSign(entry);
                    if (sign != null && sign.texture != null)
                    {
                        Rect sr = GUILayoutUtility.GetRect(36f, 36f, GUILayout.Width(36f), GUILayout.Height(36f));
                        Rect tr = sign.textureRect;
                        Texture2D tex = sign.texture;
                        GUI.DrawTextureWithTexCoords(sr, tex, new Rect(tr.x / tex.width,
                            tr.y / tex.height, tr.width / tex.width, tr.height / tex.height), true);
                    }
                }
                if (showSupportDiagnostics)
                {
                    string crew = "";
                    object? agents = ReadProperty(squad, "Agents");
                    if (agents is IEnumerable en)
                    {
                        int total = 0, seated = 0;
                        foreach (object? agent in en)
                        {
                            if (agent == null) continue;
                            total++;
                            if (ReadProperty(agent, "Seat") != null) seated++;
                        }
                        crew = " | Sitzzuordnung " + seated + "/" + total;
                    }
                    // Reine Live-Diagnose: keine Fahrbefehle und keine Änderungen an der KI.
                    try
                    {
                        object? target = ReadMember(info, "Target");
                        object? driverPresent = ReadMember(info, "hasDriver");
                        object? engine = ReadMember(car, "EngineRunning") ?? ReadMember(info, "EngineRunning");
                        object? speed = ReadMember(car, "Speed") ?? ReadMember(info, "Speed");
                        object? nodes = ReadMember(info, "Nodes");
                        int? nodeCount = nodes is ICollection collection ? collection.Count : (int?)null;
                        string driverText = driverPresent is bool present ? (present ? "JA" : "NEIN") : "?";
                        string targetText = target != null ? "JA" : "NEIN/?";
                        string routeText = nodeCount.HasValue ? nodeCount.Value.ToString() : "?";
                        string engineText = engine is bool running ? (running ? "AN" : "AUS") : "?";
                        string speedText = speed != null ? speed.ToString() ?? "?" : "?";
                        GUILayout.Label("FAHRZEUG-KI: Fahrer=" + driverText + " | Drive=" + driving +
                            " | Ziel=" + targetText + " | Routenknoten=" + routeText, infoStyle);
                        GUILayout.Label("Motor=" + engineText + " | Speed(raw)=" + speedText +
                            " | Player=" + playerControlled + " | Von=" + parking + crew, infoStyle);
                         // Zusätzliche, ausschließlich lesende Diagnose der GOAP-Ausrückbedingungen.
                         GUILayout.Label(BuildDepartureDiagnostic(squad, car, squads), infoStyle);
                        if (driverPresent is bool hasDriver && !hasDriver)
                            GUILayout.Label("HINWEIS: KI erkennt keinen Fahrer im Fahrersitz.", infoStyle);
                        else if (!driving && target != null)
                            GUILayout.Label("HINWEIS: Ziel vorhanden, KI-Fahrt noch nicht aktiviert.", infoStyle);
                        else if (nodeCount == 0 && target != null)
                            GUILayout.Label("HINWEIS: Ziel vorhanden, aber keine Routenknoten erfasst.", infoStyle);
                    }
                    catch (Exception ex)
                    {
                        GUILayout.Label("KI-Diagnose nicht lesbar: " + ex.GetBaseException().Message, infoStyle);
                    }
                }
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (count == 0) GUILayout.Label("Noch keine Einheiten für diesen Einsatz erfasst.", infoStyle);
            showSupportDiagnostics = GUILayout.Toggle(showSupportDiagnostics,
                "KI-DIAGNOSE ANZEIGEN (hilfreich für RTW / WLF / POL2 / POL3)");
            GUILayout.Label(count + " Fahrzeuge diesem Einsatz zugewiesen", infoStyle);
        }

        // Prüft nur Zustände; greift weder in GOAP noch in Fahrbefehle oder Tore ein.
        private string BuildDepartureDiagnostic(object squad, object? car, List<object> squads)
        {
            try
            {
                object? agents = ReadMember(squad, "Agents");
                int total = 0, seated = 0, drivers = 0;
                if (agents is IEnumerable enumerable)
                {
                    foreach (object? agent in enumerable)
                    {
                        if (agent == null) continue;
                        total++;
                        object? seat = ReadMember(agent, "Seat");
                        if (seat == null) continue;
                        seated++;
                        if (ReadMember(seat, "IsDriver") is bool isDriver && isDriver)
                            drivers++;
                    }
                }
                string aboard = "?";
                MethodInfo? allAboard = squad.GetType().GetMethod("AllAboardVehicle",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (allAboard != null)
                {
                    object? value = allAboard.Invoke(squad, null);
                    if (value is bool b) aboard = b ? "JA" : "NEIN";
                }
                string result = "AUSRÜCKCHECK: Alle an Bord=" + aboard +
                    " | Sitze=" + seated + "/" + total + " | Fahrersitze=" + drivers;

                // DriveTo.WaitForSituationVehicle nutzt diese feste Ausfahrtreihenfolge.
                string[] order = { "ELW", "LF24", "DLK", "TLF", "KEF", "WLF", "RTW", "NEF", "HLF", "TLF3000" };
                string name = ReadString(squad, "Name");
                int index = Array.IndexOf(order, name);
                string blockers = "";
                if (index > 0)
                {
                    object? situation = ReadMember(squad, "Situation");
                    foreach (object other in squads)
                    {
                        if (ReferenceEquals(other, squad) ||
                            !ReferenceEquals(ReadMember(other, "Situation"), situation)) continue;
                        string otherName = ReadString(other, "Name");
                        int otherIndex = Array.IndexOf(order, otherName);
                        if (otherIndex < 0 || otherIndex > index) continue;
                        string otherStatus = ReadString(other, "Status");
                        if (otherStatus != "EmergencyCall" && otherStatus != "DriveTo") continue;
                        object? otherCar = FindSquadVehicle(other);
                        object? motor = ReadMember(otherCar, "MotorState");
                        // Ohne zuverlässigen LeaveFirestation-State nur mögliche Blockade melden.
                        if (motor != null && motor.ToString() == "OFF")
                            blockers += (blockers.Length > 0 ? ", " : "") + otherName;
                    }
                }
                result += "\nAUSFAHRT-REIHENFOLGE: " +
                    (index < 0 ? "nicht in Standardliste" :
                     blockers.Length > 0 ? "möglicher Vorgänger mit Motor AUS: " + blockers :
                     "kein Vorgänger mit Motor AUS erkannt (weitere Wartezustände möglich)");
                if (drivers == 0) result += "\nHINWEIS: Kein zugewiesener Fahrersitz beim Squad-Agenten erkannt.";
                else if (aboard == "NEIN") result += "\nHINWEIS: Originalprüfung AllAboardVehicle noch nicht erfüllt.";
                return result;
            }
            catch (Exception ex)
            {
                return "AUSRÜCKCHECK nicht lesbar: " + ex.GetBaseException().Message;
            }
        }

        private object? FindSquadVehicle(object squad)
        {
            Type? wm = typeof(SituationController).Assembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
            if (wm == null) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in wm.GetMethods(flags))
                if (method.Name == "GetVehicle" && method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsInstanceOfType(squad))
                    try { return method.Invoke(null, new object[] { squad }); } catch { return null; }
            return null;
        }

        private void DispatchSupport(SituationController controller, List<object> squads, object situation)
        {
            if (!controller.IsServer || !controller.IsSituationActive ||
                !ReferenceEquals(GetCurrentSituation(GetLiveSquads()), situation)) return;
            int dispatched = 0;
            try
            {
                foreach (object squad in squads)
                {
                    string name = ReadString(squad, "Name");
                    if (!supportSelected.Contains(name)) continue;
                    if (ReadProperty(squad, "Situation") != null || ReadString(squad, "Status") != "DutyHome") continue;
                    Type? contract = null;
                    foreach (Type iface in squad.GetType().GetInterfaces())
                        if (iface.FullName == "com.crenetic.Notruf2019.AI.ISquad") { contract = iface; break; }
                    MethodInfo? call = contract?.GetMethod("CallAsSupport");
                    if (call == null) { ; continue; }
                    try
                    {
                        call.Invoke(squad, new object[] { situation });
                        dispatched++;
                        ;
                    }
                    catch (Exception) { ; }
                }
                statusMessage = dispatched + " Einheit(en) zur Nachalarmierung übergeben";
            }
            catch (Exception ex)
            {
                statusMessage = "Nachalarmierung fehlgeschlagen: " + ex.GetBaseException().Message;
                ;
            }
            finally { supportSelected.Clear(); }
        }

        // ORIGINAL-EINSÄTZE AUS DEM SPIEL LADEN
    }
}
