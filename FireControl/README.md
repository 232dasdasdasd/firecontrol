# Fire Control 3.5.0 – Modulare Projektstruktur (erste sichere Aufteilung)

**Basis:** die zuletzt funktionierende `FireControl_3.5.0_Clean_Release_Kompilierfix.cs`.

## Installation
1. **Backup** des bisherigen Visual-Studio-Projekts anlegen.
2. Die alte `Class1.cs` aus dem Projekt **entfernen** (sonst doppelte Definitionen).
3. Die `Main.cs` und alle Ordner aus diesem Paket in den Projektordner kopieren.
4. In einem SDK-Style `.csproj` werden `**/*.cs`-Dateien automatisch eingebunden. Falls dein Projekt explizite `<Compile Include>`-Einträge verwendet, die neuen Dateien im Projekt hinzufügen.
5. Alle bestehenden DLL-Referenzen, BepInEx-, Harmony- und Unity-Abhängigkeiten unverändert lassen.
6. Kompilieren und die Funktionen im Spiel testen.

## Architektur
Alle Dateien enthalten Teile derselben `public partial class ModMenu : BaseUnityPlugin`. Dadurch sind **bestehende private Felder, Methoden und Harmony-Patches weiterhin direkt erreichbar**, ohne riskante Umbenennung oder Duplizierung. `Main.cs` enthält den einzigen BepInEx-Plugin-Eintrag und die zentralen Unity-Lebenszyklusmethoden.

Die Aufteilung ist **Phase 1 (verhaltensbewahrende Dateitrennung)**. Sie ist noch **keine vollständige Entkopplung in unabhängige Fahrzeugmodule**: dafür müssen die tatsächlichen Feld- und Methodenabhängigkeiten anschließend schrittweise in Services/Interfaces überführt werden. Besonders `PoliceVehicleController.cs` und `MainMenu.cs` sind noch groß. Neue Fahrzeuge sollten erst nach Einführung einer zentralen Fahrzeugregistrierung als separate Module hinzugefügt werden.

## Wichtig
- Es wurden **keine bestehenden Methoden oder Felder gelöscht oder inhaltlich umgeschrieben**; nur die Klasse in `partial` aufgeteilt.
- Die ursprünglichen Codeabschnitte wurden in identischer Reihenfolge und mit identischem Inhalt übernommen.
- Ein vollständiger Build ist hier ohne deine lokale `.csproj` und Spiel-DLLs nicht möglich.
- Die vorhandenen Warnungsunterdrückungen sind unverändert übernommen.
