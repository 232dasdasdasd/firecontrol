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

        private static object? ReadProperty(object? obj, string propertyName)
        {
            if (obj == null) return null;
            PropertyInfo? property = obj.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null ? property.GetValue(obj) : null;
        }

        // Spielversionen verwenden teils Properties, teils Felder.
        // Fehlende Mitglieder bleiben in der Diagnose als '?' sichtbar.
        private static object? ReadMember(object? obj, string name)
        {
            if (obj == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = obj.GetType();
            try
            {
                PropertyInfo? property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(obj);
                FieldInfo? field = type.GetField(name, flags);
                return field?.GetValue(obj);
            }
            catch { return null; }
        }

        private static string ReadString(object? obj, string propertyName)
        {
            object? value = ReadProperty(obj, propertyName);
            return value?.ToString() ?? "";
        }

        private void RefreshVehicles()
        {
            vehicles.Clear();
            expandedVehicle = -1;

            try
            {
                Assembly gameAssembly = typeof(SituationController).Assembly;
                Type? aiGlobalType = gameAssembly.GetType("com.crenetic.Notruf2019.AI.AIGlobal");
                Type? wmHelperType = gameAssembly.GetType("com.crenetic.Notruf2019.AI.WMHelper");
                if (aiGlobalType == null || wmHelperType == null)
                {
                    statusMessage = "AI-Klassen nicht gefunden";
                    return;
                }

                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo? squadsProperty = aiGlobalType.GetProperty("Squads", staticFlags);
                // Manche Spielversionen speichern Squads als Feld statt als Property.
                object? squadsValue = squadsProperty != null
                    ? squadsProperty.GetValue(null)
                    : aiGlobalType.GetField("Squads", staticFlags)?.GetValue(null);
                IEnumerable? squads = squadsValue as IEnumerable;
                if (squads == null)
                {
                    statusMessage = "Keine Einheiten verfügbar";
                    return;
                }

                MethodInfo? getVehicle = null;
                foreach (MethodInfo candidate in wmHelperType.GetMethods(staticFlags))
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (candidate.Name == "GetVehicle" && parameters.Length == 1 &&
                        parameters[0].ParameterType.Name == "ISquad")
                    {
                        getVehicle = candidate;
                        break;
                    }
                }
                if (getVehicle == null)
                {
                    statusMessage = "GetVehicle nicht gefunden";
                    return;
                }

                foreach (object? item in squads)
                {
                    if (item == null) continue;
                    object squad = item;
                    try
                    {
                        object? vehicle = getVehicle.Invoke(null, new object[] { squad });
                        if (vehicle == null) continue;

                        VehicleEntry entry = new VehicleEntry();
                        // UnityEngine.Object.name ist die echte Objektbezeichnung.
                        if (vehicle is UnityEngine.Object unityVehicle && unityVehicle != null)
                            entry.VehicleName = unityVehicle.name;
                        if (string.IsNullOrWhiteSpace(entry.VehicleName))
                            entry.VehicleName = ReadString(vehicle, "Name");
                        entry.SquadName = ReadString(squad, "Name");
                        if (string.IsNullOrWhiteSpace(entry.SquadName))
                            entry.SquadName = "Unbekannte Einheit";
                        entry.Status = ReadString(squad, "Status");
                        entry.VehicleType = DetectVehicleType(entry.VehicleName + " " + entry.SquadName);
                        entry.VehicleObject = vehicle is Component vehicleComponent ? vehicleComponent.gameObject : null;
                        if (string.IsNullOrWhiteSpace(entry.VehicleName))
                            entry.VehicleName = entry.SquadName;

                        IEnumerable? agents = ReadProperty(squad, "Agents") as IEnumerable;
                        if (agents != null)
                        {
                            foreach (object? agent in agents)
                            {
                                if (agent == null) continue;
                                string name = ReadString(agent, "Name");
                                string role = ReadString(agent, "LocalizedRole");
                                if (string.IsNullOrWhiteSpace(role))
                                    role = ReadString(agent, "Role");
                                if (string.IsNullOrWhiteSpace(name))
                                    name = "Unbekannte Einsatzkraft";
                                entry.Crew.Add(string.IsNullOrWhiteSpace(role) ? name : name + "  |  " + role);
                                entry.CrewAvatars.Add(ReadProperty(agent, "Avatar"));
                                entry.CrewRoles.Add(ReadString(agent, "Role"));
                                entry.CrewSigns.Add(null);
                            }
                        }
                        vehicles.Add(entry);
                    }
                    catch (Exception)
                    {
                        ;
                    }
                }

                snapshotPreparationRequested = true;
                snapshotPreparationAttempted = false;
                RefreshOriginalVehiclePictures();
                RefreshOriginalCrewSigns();
                statusMessage = vehicles.Count + " Fahrzeuge mit Personal geladen";
                ;
            }
            catch (Exception)
            {
                ;
                statusMessage = "Fehler beim Laden der Fahrzeuge";
            }
        }

        // Nutzt die offizielle Auswahlfunktion des Spiels statt Steuerungsrechte selbst zu verändern.
        private void PlayAsCrewMember(object? avatar, string crewName)
        {
            try
            {
                if (avatar == null || (avatar is UnityEngine.Object obj && obj == null))
                {
                    statusMessage = "Feuerwehrmann derzeit nicht verfügbar";
                    return;
                }

                object? spawned = ReadProperty(avatar, "IsSpawned");
                if (spawned is bool isSpawned && !isSpawned)
                {
                    statusMessage = "Feuerwehrmann ist noch nicht gespawnt";
                    return;
                }

                object? networkId = ReadProperty(avatar, "NetworkObjectId");
                if (networkId == null)
                {
                    statusMessage = "Netzwerk-ID nicht gefunden";
                    return;
                }

                Type? playerType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    playerType = assembly.GetType("com.crenetic.Notruf2019.Network.Player", false);
                    if (playerType != null) break;
                }
                if (playerType == null)
                {
                    statusMessage = "Spielersteuerung nicht gefunden";
                    return;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo? localProperty = playerType.GetProperty("Local", flags);
                if (localProperty?.GetValue(null) == null)
                {
                    statusMessage = "Noch kein lokaler Spieler aktiv";
                    return;
                }

                MethodInfo? select = playerType.GetMethod("SetLocalAvatar", flags,
                    null, new Type[] { typeof(ulong), typeof(bool) }, null);
                if (select == null)
                {
                    statusMessage = "Avatar-Auswahlfunktion nicht gefunden";
                    return;
                }

                ulong id = Convert.ToUInt64(networkId);
                select.Invoke(null, new object[] { id, false });
                statusMessage = "Spielerwechsel angefordert: " + crewName;
                ;
                CloseMenu();
            }
            catch (Exception ex)
            {
                ;
                statusMessage = "Spielerwechsel fehlgeschlagen: " + ex.GetBaseException().Message;
            }
        }

        // Nur beim Laden der Liste ermitteln: keine teure Reflection pro GUI-Frame.
        private static string DetectVehicleType(string name)
        {
            string n = name.ToUpperInvariant();
            if (n.Contains("DLK") || n.Contains("DREHLEITER")) return "DLK";
            if (n.Contains("HLF")) return "HLF";
            if (n.Contains("LF24") || n.Contains("LF 24") || n.Contains("LÖSCH") || n.Contains(" LF")) return "LF";
            if (n.Contains("TLF") || n.Contains("TANKLÖSCH")) return "TLF";
            if (n.Contains("ELW") || n.Contains("EINSATZLEIT")) return "ELW";
            if (n.Contains("RTW") || n.Contains("RETTUNGSWAGEN")) return "RTW";
            if (n.Contains("KEF") || n.Contains("KLEINEINSATZ")) return "KEF";
            if (n.Contains("KDOW") || n.Contains("KOMMANDO")) return "KdoW";
            if (n.Contains("WLF") || n.Contains("WECHSELLADER")) return "WLF";
            return "FEUERWEHR";
        }

        // Nutzt die vorhandene Snapshot-Routine der originalen Fahrzeugauswahl.
        // Kein simuliertes Tab, keine UI-Umschaltung und keine eigene Renderkamera.
        // Die Spielroutine bewegt Fahrzeug-Transforms fuer den Renderdurchlauf kurzzeitig;
        // deshalb nur einmal pro angeforderter Aktualisierung und nie pro GUI-Frame.
        private void PrepareOriginalVehiclePictures()
        {
            try
            {
                Type? selectionType = typeof(SituationController).Assembly.GetType(
                    "com.crenetic.Notruf2019.UI.UnitSelection");
                if (selectionType == null) return;
                UnityEngine.Object[] selections = UnityEngine.Object.FindObjectsByType(selectionType, FindObjectsSortMode.None);
                if (selections == null || selections.Length == 0)
                {
                    ;
                    return;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo? update = null;
                foreach (MethodInfo method in selectionType.GetMethods(flags))
                {
                    if (method.Name.EndsWith("IInteractiveUI.OnUpdate", StringComparison.Ordinal)
                        && method.GetParameters().Length == 1)
                    {
                        update = method;
                        break;
                    }
                }
                if (update == null)
                {
                    ;
                    return;
                }

                foreach (UnityEngine.Object selection in selections)
                {
                    // OnUpdate erstellt die Bilder nur, wenn die Einheitenanzahl
                    // nicht zur Anzahl der bereits aufgebauten UI-Elemente passt.
                    update.Invoke(selection, new object?[] { null });
                }
                RefreshOriginalVehiclePictures();
                RefreshOriginalCrewSigns();
            }
            catch (Exception)
            {
                ;
            }
        }

        // Die Originalbilder werden vom Spiel selbst als Screenshots der 3D-Fahrzeuge
        // erzeugt und in UnitSelection.textureCache zwischengespeichert.
        // Wir lesen NUR diesen Cache; keine Kameras, kein Verschieben von Fahrzeugen.
        private void RefreshOriginalVehiclePictures()
        {
            if (vehicles.Count == 0) return;
            try
            {
                Type? selectionType = typeof(SituationController).Assembly.GetType(
                    "com.crenetic.Notruf2019.UI.UnitSelection");
                if (selectionType == null) return;

                UnityEngine.Object[] selections = UnityEngine.Object.FindObjectsByType(selectionType, FindObjectsSortMode.None);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                FieldInfo? cacheField = selectionType.GetField("textureCache", flags);
                if (cacheField == null) return;

                foreach (UnityEngine.Object selection in selections)
                {
                    IDictionary? cache = cacheField.GetValue(selection) as IDictionary;
                    if (cache == null) continue;
                    foreach (VehicleEntry entry in vehicles)
                    {
                        if (entry.VehicleObject == null || entry.OriginalPicture != null) continue;
                        if (cache.Contains(entry.VehicleObject))
                        {
                            Texture? picture = cache[entry.VehicleObject] as Texture;
                            if (picture != null) entry.OriginalPicture = picture;
                        }
                    }
                }
            }
            catch (Exception)
            {
                ;
            }
        }

        // Originale taktische Rollen-Symbole aus dem SpriteAtlas der Spiel-Fahrzeugauswahl.
        // Nur lesen, keine Änderungen an der Spieloberfläche oder den Sprites.
        private void RefreshOriginalCrewSigns()
        {
            if (vehicles.Count == 0) return;
            try
            {
                Type? selectionType = typeof(SituationController).Assembly.GetType(
                    "com.crenetic.Notruf2019.UI.UnitSelection");
                if (selectionType == null) return;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo? atlasField = selectionType.GetField("tacticalSigns", flags);
                if (atlasField == null) return;
                UnityEngine.Object[] selections = UnityEngine.Object.FindObjectsByType(selectionType, FindObjectsSortMode.None);
                foreach (UnityEngine.Object selection in selections)
                {
                    object? atlas = atlasField.GetValue(selection);
                    if (atlas == null) continue;
                    MethodInfo? getSprite = atlas.GetType().GetMethod("GetSprite",
                        BindingFlags.Instance | BindingFlags.Public, null,
                        new Type[] { typeof(string) }, null);
                    if (getSprite == null) continue;
                    foreach (VehicleEntry entry in vehicles)
                    {
                        for (int i = 0; i < entry.CrewRoles.Count; i++)
                        {
                            if (entry.CrewSigns[i] != null || string.IsNullOrEmpty(entry.CrewRoles[i]))
                                continue;
                            try
                            {
                                Sprite? sign = getSprite.Invoke(atlas,
                                    new object[] { "TZ_" + entry.CrewRoles[i] }) as Sprite;
                                if (sign != null) entry.CrewSigns[i] = sign;
                            }
                            catch (Exception) { /* Fehlendes Symbol: Text-Fallback bleibt sichtbar. */ }
                        }
                    }
                }
            }
            catch (Exception)
            {
                ;
            }
        }

        private void DrawCrewSign(VehicleEntry entry, int index)
        {
            Rect rect = GUILayoutUtility.GetRect(48f, 48f,
                GUILayout.Width(48f), GUILayout.Height(48f));
            Sprite? sprite = index < entry.CrewSigns.Count ? entry.CrewSigns[index] : null;
            if (sprite != null && sprite.texture != null)
            {
                // Atlas-Sprites sind Teil einer gemeinsamen Textur: nur ihren Bereich zeichnen.
                // Unity-GUI erwartet normalisierte UV-Koordinaten.
                try
                {
                    Rect r = sprite.textureRect;
                    Texture2D texture = sprite.texture;
                    Rect uv = new Rect(r.x / texture.width, r.y / texture.height,
                        r.width / texture.width, r.height / texture.height);
                    GUI.DrawTextureWithTexCoords(rect, texture, uv, true);
                    return;
                }
                catch (Exception) { /* z.B. eng gepacktes Atlas-Sprite */ }
            }
            // Solange der Atlas nicht bereit ist, die Rolle lesbar anzeigen.
            GUI.DrawTexture(rect, panelTexture, ScaleMode.StretchToFill);
            string role = index < entry.CrewRoles.Count ? entry.CrewRoles[index] : "";
            if (role.Length > 7) role = role.Substring(0, 7);
            GUI.Label(rect, role.Length > 0 ? role : "?", imageBadgeStyle);
        }

        private void DrawVehiclePicture(VehicleEntry entry)
        {
            Rect rect = GUILayoutUtility.GetRect(192f, 110f,
                GUILayout.Width(192f), GUILayout.Height(110f));
            GUI.DrawTexture(rect, panelTexture, ScaleMode.StretchToFill);

            Texture? image = entry.OriginalPicture;
            if (image != null)
                GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit);
            else
                GUI.Label(rect, entry.VehicleType, imageBadgeStyle);
        }

        private void DrawVehiclesTab()
        {
            GUILayout.Label("FAHRZEUGFLOTTE & PERSONAL", headingStyle);
            GUILayout.Space(6);
            GUILayout.Label("Fahrzeuge, zugeteilte Besatzung und direkter Spielerwechsel", infoStyle);
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            GUILayout.Label("SUCHE", textStyle, GUILayout.Width(65));
            vehicleSearch = GUILayout.TextField(vehicleSearch, GUILayout.Height(36), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("AKTUALISIEREN", activeButtonStyle, GUILayout.Width(190)))
                RefreshVehicles();
            GUILayout.EndHorizontal();

            int totalCrew = 0;
            for (int i = 0; i < vehicles.Count; i++) totalCrew += vehicles[i].Crew.Count;
            GUILayout.Space(8);
            GUILayout.Label(vehicles.Count + " FAHRZEUGE   |   " + totalCrew + " EINSATZKRÄFTE", headingStyle);
            GUILayout.Label("Fahrzeug anklicken, um die Besatzung anzuzeigen. Originalbilder aus dem Spiel (sobald die Fahrzeugauswahl sie erzeugt hat).", infoStyle);
            GUILayout.Space(10);

            float availableHeight = Mathf.Max(180f, windowRect.height - 390f);
            vehicleScroll = GUILayout.BeginScrollView(vehicleScroll, GUILayout.Height(availableHeight));
            if (vehicles.Count == 0)
                GUILayout.Label("Keine Fahrzeuge gefunden. Spielwelt laden und AKTUALISIEREN klicken.", textStyle);

            int shown = 0;
            for (int i = 0; i < vehicles.Count; i++)
            {
                VehicleEntry entry = vehicles[i];
                string search = vehicleSearch.Trim();
                if (search.Length > 0)
                {
                    bool match = (entry.VehicleName + " " + entry.SquadName + " " + entry.VehicleType + " " + entry.Status)
                        .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                        for (int j = 0; j < entry.Crew.Count; j++)
                            if (entry.Crew[j].IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    if (!match) continue;
                }
                shown++;
                GUILayout.BeginVertical(panelStyle);
                GUILayout.BeginHorizontal();
                DrawVehiclePicture(entry);
                GUILayout.BeginVertical();
                GUILayout.Label(entry.VehicleName, headingStyle);
                GUILayout.Label("TYP: " + entry.VehicleType + "    |    PERSONAL: " + entry.Crew.Count, textStyle);
                GUILayout.Label("EINHEIT: " + entry.SquadName, infoStyle);
                GUILayout.Label("STATUS: " + (string.IsNullOrWhiteSpace(entry.Status) ? "Nicht angegeben" : entry.Status), infoStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(expandedVehicle == i ? "PERSONAL SCHLIESSEN" : "PERSONAL ANZEIGEN", expandedVehicle == i ? activeButtonStyle : buttonStyle))
                    expandedVehicle = expandedVehicle == i ? -1 : i;
                GUILayout.Label(entry.OriginalPicture != null ? "ORIGINALBILD" : "BILD NOCH NICHT GELADEN", infoStyle);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                if (expandedVehicle == i)
                {
                    GUILayout.Space(10);
                    GUILayout.Label("ZUGETEILTE EINSATZKRÄFTE", headingStyle);
                    GUILayout.Label("Originale taktische Rollensymbole aus dem Spiel | Zuordnung zur Einheit", infoStyle);
                    if (entry.Crew.Count == 0)
                        GUILayout.Label("Keine Einsatzkräfte zugeordnet", textStyle);
                    for (int j = 0; j < entry.Crew.Count; j++)
                    {
                        GUILayout.BeginHorizontal();
                        DrawCrewSign(entry, j);
                        GUILayout.Space(8);
                        GUILayout.Label((j + 1) + ".  " + entry.Crew[j], textStyle, GUILayout.ExpandWidth(true));
                        object? avatar = j < entry.CrewAvatars.Count ? entry.CrewAvatars[j] : null;
                        if (GUILayout.Button("SPIELEN", activeButtonStyle, GUILayout.Width(135)))
                            PlayAsCrewMember(avatar, entry.Crew[j]);
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.EndVertical();
                GUILayout.Space(9);
            }
            if (shown == 0 && vehicles.Count > 0)
                GUILayout.Label("Keine Treffer für diese Suche.", textStyle);
            GUILayout.EndScrollView();
        }

        // LIVE-KARTE: Originale MapView-RenderTexture verwenden. Die F5-UI
        // selbst bleibt geschlossen; nur ihre vorhandene Kartenkamera wird aktiviert.
        private static object? ReadField(object? obj, string fieldName)
        {
            if (obj == null) return null;
            return obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
        }

    }
}
