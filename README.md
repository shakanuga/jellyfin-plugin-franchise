# Franchise Collections – Jellyfin-Plugin

Legt große, verschachtelte Sammlungen mit **Filmen und Serien** an (z. B. *Marvel → MCU → Phase 1–6*)
und optional eine **Watch-Order-Playlist**, in der Filme genau zwischen den richtigen Staffeln/Folgen stehen
(z. B. *Akte X: Staffel 1–5 → Der Film → Staffel 6–9 …*).

Mitgeliefert: **Marvel** (inkl. MCU mit 6 Phasen, X-Men/Deadpool, Spider-Man/Sony, Netflix-/ABC-Serien),
**Star Trek** (alle Serien + Filme) und **Akte X**.

## Bauen

Auf dem Unraid-Server (oder überall mit Docker) – es muss kein .NET installiert sein:

```bash
./build.sh          # Jellyfin 10.11
./build.sh 10.10    # Jellyfin 10.10
```

Ergebnis: `dist/FranchiseCollections_0.1.0.0/` mit DLL und `meta.json`.

Alternativ ins eigene GitHub-Repo pushen – die Action unter `.github/workflows/build.yml` baut beide
Varianten und stellt sie als Download (Artifact) bereit.

## Installieren

1. Jellyfin stoppen.
2. Den Ordner `FranchiseCollections_0.1.0.0` in den Plugin-Ordner von Jellyfin kopieren, also dorthin, wo
   schon andere Plugins liegen (z. B. der TheTVDB-Ordner):
   - offizielles Image `jellyfin/jellyfin`: `appdata/jellyfin/config/plugins/` bzw. `/config/plugins/`
   - linuxserver-Image: `appdata/jellyfin/data/plugins/`
3. Jellyfin starten → *Dashboard → Plugins → Franchise Collections*.
4. *Jetzt synchronisieren* klicken. Danach läuft es nach jedem Bibliotheks-Scan automatisch.

## Was passiert

- **Sammlungen** werden im normalen Bereich „Sammlungen“ angelegt. Unter-Sammlungen (Phasen) stecken in der
  übergeordneten Sammlung, tauchen aber – so ist Jellyfin gebaut – auch einzeln in der Sammlungsübersicht auf.
- Eine Sammlung wird erst angelegt, wenn mindestens ein Titel davon in der Bibliothek ist.
- Kommt ein neuer Film/eine neue Staffel dazu, wird er beim nächsten Lauf einsortiert.
- **Playlists** heißen „<Name> – Reihenfolge“ und gehören dem eingestellten Benutzer (standardmäßig für alle sichtbar).
  Serien werden darin in einzelne Folgen aufgelöst; fehlende Folgen werden übersprungen.
- Der **Bericht** auf der Einstellungsseite zeigt pro Sammlung, was noch in der Bibliothek fehlt.
- Eigene, von Hand angelegte Sammlungen werden nie angefasst (das Plugin erkennt seine eigenen an einer Markierung).

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
| `"collection": "andere-id"` | andere Definition als Unter-Sammlung; in der Playlist wird ihre Reihenfolge übernommen |
| `"title"`, `"year"` | Originaltitel + Jahr: für den Bericht und als Rückfallebene, wenn keine ID passt (`"movie": ""` = nur über Titel suchen) |
| `"playlistOnly"` / `"collectionOnly"` | Eintrag nur in Playlist bzw. nur in Sammlung |

IDs: `imdb:`, `tmdb:`, `tvdb:` – genau so, wie sie in Jellyfin bei *Metadaten bearbeiten → Externe IDs* stehen.
Folgen werden nach der Staffel-/Folgennummerierung in Jellyfin gezählt (bei dir TheTVDB), Specials (Staffel 0) nur,
wenn ein Bereich ausdrücklich in `S00` beginnt.

## Grenzen

- Entfernte Definitionen löschen ihre Sammlung/Playlist nicht automatisch – einfach in Jellyfin löschen.
- Innerhalb einer Sammlung kann Jellyfin nur nach Datum oder Name sortieren; die echte Reihenfolge steht in der Playlist.
- Die mitgelieferten Listen sind nach bestem Wissen gepflegt; IDs, die nicht passen, fängt die Titel+Jahr-Suche
  meist auf – sonst erscheint der Titel im Bericht unter „fehlt“.
