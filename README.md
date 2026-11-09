# Franchise Collections – Jellyfin-Plugin

Legt große, verschachtelte Sammlungen mit **Filmen und Serien** an (z. B. *Marvel → MCU → Phase 1–6*)
und **Watch-Order-Playlists**, in denen Filme genau zwischen den richtigen Staffeln/Folgen stehen
(z. B. *Akte X: Staffel 1–5 → Der Film → Staffel 6–9 …*).

Alles sind ganz normale Jellyfin-Sammlungen und -Playlists – funktioniert also in **jeder** App
(Browser, Wholphin, Android TV, Handy …), ohne Zusatz-Plugins oder eingeschleustes JavaScript.

Mitgeliefert:

- **Marvel** – MCU mit Phase 1–6, X-Men/Deadpool, Spider-Man (Sony), Netflix-/ABC-Serien
- **MCU – Erscheinungsreihenfolge** und **MCU – Chronologisch** – Playlists, folgengenau,
  **automatisch sortiert** nach [aaronperris.com/mcu](https://aaronperris.com/mcu/). Neue Filme/Serien
  sortieren sich von selbst ein, sobald sie dort und in deiner Bibliothek sind.
- **Marvel-Multiversum – Chronologisch** (standardmäßig aus) – inkl. Fox, Sony, Netflix, Blade …
- **Star Trek** (alle Serien + Filme) und **Akte X**

## Installieren (über den Katalog – mit Updates)

1. *Dashboard → Plugins → Repositories → +*
   - Name: `Franchise Collections`
   - URL: `https://github.com/shakanuga/jellyfin-plugin-franchise/releases/latest/download/manifest.json`
2. *Dashboard → Plugins → Katalog → Franchise Collections → Installieren*, Jellyfin neu starten.
3. *Dashboard → Plugins → Franchise Collections*: Benutzer für die Playlists auswählen, speichern,
   **Jetzt synchronisieren**. Danach läuft es nach jedem Bibliotheks-Scan von selbst.

Neue Versionen erscheinen dann automatisch im Dashboard.

Benötigt **Jellyfin 12.x**. (Für 10.11 gibt es unter *Actions* einen Test-Build zum Installieren von Hand.)

## Neue Version veröffentlichen

Im `.csproj` `<AssemblyVersion>` (und `<FileVersion>`) erhöhen, in `CHANGELOG.md` einen Abschnitt
`## 0.2.1` mit den Änderungen ergänzen und pushen. Die Action *Release* merkt, dass es die Version noch nicht
gibt, baut das Plugin und erstellt das GitHub-Release samt Katalog-Manifest.

## Was passiert

- **Sammlungen** erscheinen im normalen Bereich „Sammlungen“. Unter-Sammlungen (Phasen) stecken in der
  übergeordneten Sammlung, tauchen aber – so ist Jellyfin gebaut – auch einzeln in der Übersicht auf.
- Eine Sammlung wird erst angelegt, wenn mindestens ein Titel davon in der Bibliothek ist.
- **Playlists** bekommt jeder ausgewählte Benutzer als eigene Kopie. Ist *Gesehenes entfernen* an (Standard),
  verschwindet ein Film/eine Folge ca. 20 Sekunden nach dem Zu-Ende-Schauen – oben steht immer das Nächste,
  einfach die Playlist abspielen. „Als ungesehen markieren“ bringt es zurück.
- Die automatischen MCU-Playlists nehmen nur, was auch in der MCU-Sammlung steckt („scope“) – die Liste von
  aaronperris.com bestimmt nur die **Reihenfolge**. Ist die Seite mal nicht erreichbar, wird die zuletzt
  geladene Kopie benutzt; ohne Kopie bleiben die Playlists unverändert (werden nie geleert).
- Der **Bericht** auf der Einstellungsseite zeigt pro Sammlung, was noch fehlt.
- Eigene, von Hand angelegte Sammlungen werden nie angefasst.

## Kleinigkeiten korrigieren

- **Etwas soll raus:** bei einer mitgelieferten Sammlung *Als eigene Kopie bearbeiten* klicken und ein
  `"exclude"` ergänzen, z. B. `"exclude": ["Werewolf by Night", "What If...? S02", "Loki S01E03"]`.
- **Eine Serie wird nicht erkannt:** im Feld *Namens-Zuordnungen* eintragen, z. B. `Daredevil = Marvel's Daredevil`.
- **Reihenfolge/Inhalt ändern:** *Als eigene Kopie bearbeiten* – deine Kopie (gleiche `id`) ersetzt die mitgelieferte.

## Bilder

Ohne Bild erzeugt Jellyfin automatisch eine Collage aus den Postern. Eigene Bilder:

- Datei `<id>-poster.jpg` bzw. `<id>-backdrop.jpg` (auch .png/.webp) in
  `…/plugins/configurations/FranchiseCollections/` legen, z. B. `mcu-phase-1-poster.jpg`, oder
- in der Definition `"poster": "https://…"` / `"backdrop": "https://…"` angeben, oder
- `"tmdbCollectionId": "…"` setzen und in Jellyfin bei der Sammlung *Metadaten aktualisieren* → Bilder von TMDb.

Jedes Bild wird nur einmal gesetzt (bzw. wieder, wenn sich Datei/URL ändert) – wenn du in Jellyfin
später selbst ein anderes Bild wählst, bleibt es.

## Eigene Definitionen

Auf der Einstellungsseite (JSON-Feld) oder als `*.json` im Ordner `…/plugins/configurations/FranchiseCollections/`.
Gleiche `id` wie eine mitgelieferte Definition überschreibt diese (z. B. um die Star-Trek-Reihenfolge anzupassen).

```jsonc
{
  "id": "stargate",                 // eindeutig, klein, ohne Leerzeichen
  "name": "Stargate",
  "overview": "Text für die Sammlung",
  "sortName": "Stargate",           // optional
  "playlist": true,                 // Watch-Order-Playlist anlegen
  "playlistName": "Stargate – Reihenfolge",  // optional
  "displayOrder": "PremiereDate",   // oder "SortName" (sinnvoll bei Unter-Sammlungen)
  "entries": [
    { "movie": "imdb:tt0111282", "title": "Stargate", "year": 1994 },
    { "series": "imdb:tt0118480|tvdb:72449", "title": "Stargate SG-1", "year": 1997, "to": "S10" },
    { "movie": "imdb:tt0942903", "title": "Stargate: The Ark of Truth", "year": 2008 },
    { "movie": "imdb:tt1078940", "title": "Stargate: Continuum", "year": 2008 },
    { "series": "imdb:tt0374455", "title": "Stargate Atlantis", "year": 2004 }
  ]
}
```

Eintragsarten:

| Feld | Bedeutung |
|---|---|
| `"movie": "imdb:tt…"` | ein Film. Mehrere IDs mit `\|` trennen: `"imdb:tt0120902\|tmdb:846"` |
| `"series": "tvdb:…"` | eine Serie, optional mit `"from": "S06"` / `"to": "S05E20"` |
| `"tmdbCollection": "263"` | alle Filme einer TMDb-Collection, die du hast (nach Datum) |
| `"source": "aaronperris-mcu"` | automatische MCU-Liste; dazu `"order": "release"` / `"chronological"` / `"sacred"`, optional `"scope": "mcu"` (nur was in dieser Sammlung ist) und `"canonOnly": false` |
| `"collection": "andere-id"` | andere Definition als Unter-Sammlung; in der Playlist wird ihre Reihenfolge übernommen |
| `"title"`, `"year"` | Originaltitel + Jahr: für den Bericht und als Rückfallebene, wenn keine ID passt (`"movie": ""` = nur über Titel suchen) |
| `"playlistOnly"` / `"collectionOnly"` | Eintrag nur in Playlist bzw. nur in Sammlung |

IDs: `imdb:`, `tmdb:`, `tvdb:` – genau so, wie sie in Jellyfin bei *Metadaten bearbeiten → Externe IDs* stehen.
Folgen werden nach der Staffel-/Folgennummerierung in Jellyfin gezählt (bei dir TheTVDB), Specials (Staffel 0) nur,
wenn ein Bereich ausdrücklich in `S00` beginnt.

Auf Definitionsebene gibt es außerdem `"exclude": [...]` (siehe oben) und `"defaultEnabled": false`.

## Grenzen

- Entfernte Definitionen löschen ihre Sammlung/Playlist nicht automatisch – einfach in Jellyfin löschen.
- Innerhalb einer Sammlung kann Jellyfin nur nach Datum oder Name sortieren; die echte Reihenfolge steht in der Playlist.
- Die mitgelieferten Listen sind nach bestem Wissen gepflegt; IDs, die nicht passen, fängt die Titel+Jahr-Suche
  meist auf – sonst erscheint der Titel im Bericht unter „fehlt“.
