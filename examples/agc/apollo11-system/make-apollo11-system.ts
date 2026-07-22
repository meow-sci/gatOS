#!/usr/bin/env bun
/**
 * make-apollo11-system.ts — builds an "Apollo 11 (July 1969)" KSA system mod from YOUR
 * installed game content, so the mission starts with Earth and the Moon where they really
 * were at Apollo 11 launch (1969-07-16 13:32:00 UTC = sim time zero).
 *
 * Why a generator instead of a shipped XML: the full Earth/Luna definitions (terrain, clouds,
 * biomes, textures) are KSA's own content — we extract them from the player's install at
 * build time and patch ONLY the orbit/rotation blocks, so nothing proprietary lives in this
 * repo and a game update's new terrain data is picked up automatically.
 *
 *   bun make-apollo11-system.ts --content "<KSA>/Content/Core" --out "<KSA>/Content/Apollo11"
 *
 * Then start KSA and pick "Apollo 11 (July 1969)" in the system/modpack selection.
 *
 * Ephemeris notes (epoch JD 2440419.064 = 1969-07-16 13:32 UT, launch):
 * - Moon (geocentric, ecliptic frame, Meeus mean elements at epoch): node Ω=354.21°,
 *   arg-perigee ω=289.7°, i=5.145°, M=214.46° ⇒ next perigee +11.139 d (TimeAtPeriapsis
 *   +962,438 s — real July 1969: apogee Jul 14, perigee Jul 28 ✓); a/e from that month's
 *   actual apsides (404,950 / 366,000 km) ⇒ a=385,475 km, e=0.0505. Apollo arrived (Jul 19)
 *   with the Moon at ≈389,000 km — reproduced by construction.
 * - Earth (heliocentric): aphelion was Jul 4-5 1969 ⇒ M=191.8°, next perihelion +170.6 d
 *   (+14,741,000 s); ϖ=102.6° with the stock node 200.1° ⇒ ω=262.5°; a=1.49598e8 km,
 *   e=0.0167. Subsolar longitude at 13:32 UT ≈ 23.4°W ⇒ InitialParentFacingLongitude=-23.4.
 * - Moon spin: tidally locked; Cassini tilt 1.5424°; pole azimuth follows the 1969 node
 *   (≈ Ω−360 = −5.8°); mean libration zero ⇒ InitialParentFacingLongitude=0.
 * - Landmarks: nothing added — the stock content already carries "CCSFS LC-39A", "Houston",
 *   and the "Apollo11" landing-site marker on Luna (0.67408°N 23.47297°E — exactly the
 *   agc-padload default site).
 *
 * Mission clock cheat-sheet on this epoch (sim seconds from t=0):
 *   TLI ≈ +9,930 · LOI-1 ≈ +273,000 · undock ≈ +361,000 · PDI ≈ +367,000 ·
 *   touchdown (historical) = +369,940 (Jul 20 20:17:40 UT) — `agc-padload --tland=369940`.
 */

import * as fs from "node:fs";
import * as path from "node:path";

function arg(name: string, def?: string): string {
  const hit = process.argv.find((a) => a.startsWith(`--${name}=`));
  if (hit) return hit.split("=").slice(1).join("=");
  const i = process.argv.indexOf(`--${name}`);
  if (i >= 0 && process.argv[i + 1]) return process.argv[i + 1];
  if (def !== undefined) return def;
  console.error(`missing --${name}`);
  process.exit(2);
}

const contentDir = arg("content");
const outDir = arg("out");

const astro = fs.readFileSync(path.join(contentDir, "Astronomicals.xml"), "utf8");

/** Extracts one balanced top-level element `<Tag Id="id" ...> ... </Tag>` from the library. */
function extractBody(tag: string, id: string): string {
  const openRe = new RegExp(`<${tag}\\s+Id="${id}"[^>]*>`);
  const m = openRe.exec(astro);
  if (!m) throw new Error(`no <${tag} Id="${id}"> in Astronomicals.xml`);
  const start = m.index;
  let depth = 0;
  const tokRe = new RegExp(`<${tag}\\b[^>]*?(/?)>|</${tag}>`, "g");
  tokRe.lastIndex = start;
  for (let t = tokRe.exec(astro); t; t = tokRe.exec(astro)) {
    if (t[0].startsWith(`</`)) {
      depth--;
      if (depth === 0) return astro.slice(start, t.index + t[0].length);
    } else if (t[1] !== "/") {
      depth++;
    }
  }
  throw new Error(`unbalanced <${tag} Id="${id}">`);
}

/** Replaces the FIRST <Orbit ...>...</Orbit> / <Rotation ...>...</Rotation> block. */
function replaceBlock(body: string, tag: string, replacement: string): string {
  const re = new RegExp(`<${tag}(\\s[^>]*)?>[\\s\\S]*?</${tag}>`);
  if (!re.test(body)) throw new Error(`no <${tag}> block to replace`);
  return body.replace(re, replacement);
}

const sol = extractBody("StellarBody", "Sol");

let earth = extractBody("AtmosphericBody", "Earth");
earth = replaceBlock(
  earth,
  "Orbit",
  `<Orbit>
        <!-- Apollo 11 epoch: 1969-07-16 13:32:00 UTC = sim t=0 (see make-apollo11-system.ts) -->
        <SemiMajorAxis Km="1.49598023E+08" />
        <Inclination Degrees="4.495777668608611E-03" />
        <Eccentricity Value="0.0167086" />
        <LongitudeOfAscendingNode Degrees="2.001005464352027E+02" />
        <ArgumentOfPeriapsis Degrees="262.5" />
        <TimeAtPeriapsis Seconds="14741000" />
    </Orbit>`,
);
earth = replaceBlock(
  earth,
  "Rotation",
  `<Rotation DefinitionFrame="Ecliptic">
        <SiderealPeriod Hours="23.9344695944"/>
        <Tilt Degrees="23.441522" />
        <Azimuth Degrees="-0.363633" />
        <!-- Subsolar longitude at 13:32 UT launch: ~23.4°W -->
        <InitialParentFacingLongitude Degrees="-23.4" />
    </Rotation>`,
);
// Landmarks: the stock content already carries "CCSFS LC-39A", "Houston", and the "Apollo11"
// landing-site marker on Luna (0.67408°N 23.47297°E — the agc-padload default), so nothing to
// add.

let luna = extractBody("PlanetaryBody", "Luna");
luna = replaceBlock(
  luna,
  "Orbit",
  `<Orbit DefinitionFrame="Ecliptic">
        <!-- Meeus mean elements at the Apollo 11 launch epoch; a/e from the real July 1969
             apsides (apogee Jul 14 404,950 km · perigee Jul 28 366,000 km) -->
        <SemiMajorAxis Km="385475" />
        <Inclination Degrees="5.145" />
        <Eccentricity Value="0.0505" />
        <LongitudeOfAscendingNode Degrees="354.21" />
        <ArgumentOfPeriapsis Degrees="289.7" />
        <TimeAtPeriapsis Seconds="962438" />
    </Orbit>`,
);
luna = replaceBlock(
  luna,
  "Rotation",
  `<Rotation DefinitionFrame="Ecliptic">
        <IsTidallyLocked Value="true" />
        <Tilt Degrees="1.5424" />
        <Azimuth Degrees="-5.8" />
        <InitialParentFacingLongitude Degrees="0" />
    </Rotation>`,
);

const system = `<?xml version="1.0" encoding="utf-8"?>
<System Id="Apollo11">
    <DisplayName Value="Apollo 11 (July 1969)" />
    <GalacticPlane>
      <RightAscension Degrees="192.86" />
      <Declination Degrees="27.13" />
    </GalacticPlane>
    ${sol}
    ${earth.replace(/ Parent="Sol"/, ` Parent="Sol" HomeBody="true"`)}
    ${luna}
</System>
`;

const modToml = `# Apollo 11 (July 1969) — Earth and the Moon at the Apollo 11 launch epoch.
# Generated by gatOS examples/agc/apollo11-system/make-apollo11-system.ts — do not edit;
# regenerate after game updates so terrain/texture data stays current.
name = "Apollo 11 (July 1969)"
systems = [ "Apollo11.xml" ]
`;

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, "Apollo11.xml"), system);
fs.writeFileSync(path.join(outDir, "mod.toml"), modToml);
console.log(`wrote ${path.join(outDir, "Apollo11.xml")} (${(system.length / 1024).toFixed(0)} KiB) + mod.toml`);
console.log(`Earth: aphelion-relative M=191.8°, next perihelion +170.6 d, subsolar 23.4°W`);
console.log(`Luna:  Ω=354.21° ω=289.7° i=5.145° e=0.0505 a=385,475 km, perigee +11.14 d`);
console.log(`touchdown (historical) = sim t +369,940 s  →  agc-padload --tland=369940`);
