#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Notruf112ModMenu
{
    // Eigenstaendiges BepInEx-Plugin. Keine Aenderung in Main.cs erforderlich.
    // Entfernt ausschliesslich direkte statische Fahrzeug-Kinder aus dem AKTIVEN
    // Feuerwehrwachen-Container: _Firestation/Building/_Static_Vehicles.
    // Die deaktivierte Vorlagen-Hierarchie _Static_Firestation bleibt unangetastet.
    [BepInPlugin("de.notruf112.firecontrol.staticvehicles.diagnostics", "Fire Control Static Vehicle Removal", "1.1.0")]
    public sealed class FCStaticVehiclesDiagnostics : BaseUnityPlugin
    {
        private float nextScan;
        private readonly HashSet<int> processed = new HashSet<int>();
        private bool stationVehiclesRemoved;
        private int remainingStartupChecks;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            nextScan = 0f;
            remainingStartupChecks = 30;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            nextScan = 0f;
            remainingStartupChecks = 30;
            stationVehiclesRemoved = false;
            processed.Clear();
        }

        private void Update()
        {
            // Keine globale Unity-Objektsuche mehr im Dauerbetrieb.
            // Spaet geladene Wachen werden in der Startphase nachtraeglich bereinigt.
            if (stationVehiclesRemoved || remainingStartupChecks <= 0 || Time.unscaledTime < nextScan) return;
            remainingStartupChecks--;
            nextScan = Time.unscaledTime + 2f;
            RemoveStationVehicles();
        }

        private void RemoveStationVehicles()
        {
            try
            {
                GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < objects.Length; i++)
                {
                    GameObject container = objects[i];
                    if (container == null || container.name != "_Static_Vehicles") continue;
                    if (!container.scene.IsValid() || !container.scene.isLoaded) continue;

                    Transform parent = container.transform.parent;
                    if (parent == null || parent.name != "Building") continue;
                    Transform root = parent.parent;
                    if (root == null || root.name != "_Firestation") continue;
                    // Nicht die deaktivierte _Static_Firestation-Vorlage veraendern.
                    if (!root.gameObject.activeInHierarchy) continue;

                    Transform t = container.transform;
                    stationVehiclesRemoved = true;
                    // Rueckwaerts, damit das Entfernen von Kindern stabil bleibt.
                    for (int c = t.childCount - 1; c >= 0; c--)
                    {
                        Transform child = t.GetChild(c);
                        if (child == null) continue;
                        GameObject go = child.gameObject;
                        if (!go.name.StartsWith("Static_", StringComparison.OrdinalIgnoreCase)) continue;
                        int id = go.GetInstanceID();
                        if (!processed.Add(id)) continue;

                        // Sofort unsichtbar, danach am Frame-Ende aus der Szene entfernen.
                        go.SetActive(false);
                        Destroy(go);
                    }
                }
            }
            catch (Exception)
            {
                // Keine Logausgabe; ein fehlgeschlagener Scan wird spaeter erneut versucht.
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}
