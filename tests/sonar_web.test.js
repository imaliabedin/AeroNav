// Tests the website's sonar navigation (sonar-core.js) without a browser:  node tests/sonar_web.test.js
'use strict';
const assert = require('assert');
const { SonarFlight, SonarSensor, SonarBelief } = require('../sonar-core.js');

// The portal's 8 obstacle blocks, as in portal.js: [x, z, width, depth, height], standing on y = -0.45.
const LAYOUT = [
  [-23, -16, 9, 9, 7], [-7, -23, 7, 13, 10], [14, -14, 12, 8, 8],
  [24, 7, 8, 11, 12], [6, 22, 10, 9, 7], [-20, 20, 7, 12, 9],
  [-2, 3, 6, 8, 11], [32, -29, 6, 7, 7],
];
const block = ([x, z, w, d, h], name) => ({ name, min: [x - w / 2, -0.45, z - d / 2], max: [x + w / 2, h - 0.45, z + d / 2] });
const BLOCKS = LAYOUT.map((b, i) => block(b, `block ${i + 1}`));

// Builder props as the boxes of their parts (sizes from builder.js).
const streetLight = (x, z) => [
  { name: 'street light pole', min: [x - 0.14, 0, z - 0.14], max: [x + 0.14, 4.7, z + 0.14] },
  { name: 'street light arm', min: [x - 0.07, 4.49, z - 0.06], max: [x + 0.93, 4.62, z + 0.06] },
];
const car = (x, z) => [{ name: 'car', min: [x - 0.93, 0.31, z - 2], max: [x + 0.93, 1.51, z + 2] }];
const tree = (x, z) => [
  { name: 'tree trunk', min: [x - 0.42, 0, z - 0.42], max: [x + 0.42, 2.1, z + 0.42] },
  { name: 'tree crown', min: [x - 1.7, 1.4, z - 1.7], max: [x + 2.45, 4.8, z + 1.7] },
];

const START = [-38, 8, 35], GOAL = [34, 1, 31];
const BOUNDS = { min: [-46, 1, -46], max: [46, 18, 46] };

function fly(boxes, seed, speed = 5, maxSeconds = 120) {
  const f = new SonarFlight({ start: START, goal: GOAL, bounds: BOUNDS, speed, seed });
  f.start(boxes);
  const dt = 1 / 60;
  let t = 0, pulses = 0, msSum = 0, msMax = 0, planMax = 0;
  while (f.running && t < maxSeconds) {
    const replansBefore = f.replans;
    const t0 = Date.now();
    const fired = f.step(dt, boxes);
    if (f.replans > replansBefore) planMax = Math.max(planMax, Date.now() - t0);
    for (const p of fired) { pulses++; msSum += p.ms; msMax = Math.max(msMax, p.ms); }
    t += dt;
  }
  return { status: f.status, replans: f.replans, collisions: f.collisions, lastHit: f.lastHit, moves: f.moves,
           seconds: t, pulses, msAvg: msSum / Math.max(1, pulses), msMax, planMax, beliefCells: f.belief.occupiedList.size };
}

const mast = (x, z, height) => [{ name: 'mast', min: [x - 0.14, -0.45, z - 0.14], max: [x + 0.14, height, z + 0.14] }];

// Each scenario puts unknown obstacles where the drone actually flies (it cruises at y = 8 and lands at y = 1).
const scenarios = {
  'portal blocks only': BLOCKS,
  'blocks + cars, trees and street lights around the landing pad': BLOCKS.concat(
    car(26, 31), car(34, 23), tree(28, 37), streetLight(40, 28), streetLight(29, 25), tree(40, 37), car(22, 34)),
  'blocks + fence of 12 m masts (0.28 thick, 3 apart) across the map': BLOCKS.concat(
    ...Array.from({ length: 31 }, (_, i) => mast(-14, -45 + i * 3, 12))),
  'blocks + surprise wall with one gap': BLOCKS.concat([
    { name: 'wall north', min: [9, -0.45, 8], max: [10, 14, 46] },
    { name: 'wall south', min: [9, -0.45, -46], max: [10, 14, 0] },
  ]),
};

let failures = 0;
for (const [name, boxes] of Object.entries(scenarios)) {
  const runs = [];
  for (let seed = 1; seed <= 8; seed++) runs.push(fly(boxes, seed));
  const ok = runs.filter(r => r.status === 'landed' && r.collisions === 0).length;
  console.log(`${name}: landed without collision ${ok}/${runs.length}; replans ${runs.map(r => r.replans).join(',')}; ` +
    `time ${runs.map(r => r.seconds.toFixed(0)).join(',')} s; pulse ${(runs.reduce((a, r) => a + r.msAvg, 0) / runs.length).toFixed(2)} ms avg, ` +
    `${Math.max(...runs.map(r => r.msMax)).toFixed(1)} ms max; slowest replan frame ${Math.max(...runs.map(r => r.planMax))} ms`);
  for (const r of runs.filter(r => r.status !== 'landed' || r.collisions)) console.log('   ', JSON.stringify(r));
  if (ok !== runs.length) failures++;
}

// The planner must not know the obstacles: with no sensing at all it flies the straight route and the
// collision check (not the planner) stops it at the first block in the way.
{
  const f = new SonarFlight({ start: START, goal: GOAL, bounds: BOUNDS, speed: 5, seed: 1, sensor: { maxRange: 0.01, falseEchoProbability: 0 } });
  const wall = [{ name: 'wall', min: [0, -0.45, 20], max: [1, 18, 46] }];
  f.start(wall);
  for (let t = 0; f.running && t < 60; t += 1 / 60) f.step(1 / 60, wall);
  console.log(`blind flight: status ${f.status}, hit ${f.lastHit}, stopped at x = ${f.pos[0].toFixed(2)} (wall face at 0, body half-width 3.25)`);
  assert.strictEqual(f.status, 'collision');
  assert.ok(f.pos[0] <= 0 - 3.25 + 1e-6, 'stopped before touching');
}

// False echoes in open space: phantom cells must clear quickly.
{
  const s = new SonarSensor({ seed: 3 });
  const b = new SonarBelief(BOUNDS);
  let longest = 0;
  const since = new Map();
  for (let i = 0; i < 1000; i++) {
    b.integrate(s.pulse([0, 8, 0], []), s.settings.maxRange);
    for (const c of b.occupiedList) if (!since.has(c)) since.set(c, i);
    for (const [c, t0] of since) if (!b.occupiedList.has(c)) { longest = Math.max(longest, i - t0); since.delete(c); }
  }
  for (const t0 of since.values()) longest = Math.max(longest, 1000 - t0);
  console.log(`open space, 1000 pulses: longest-lived phantom cell ${longest} pulses`);
  assert.ok(longest <= 20);
}

if (failures) { console.log(`FAILED (${failures} scenario(s))`); process.exit(1); }
console.log('PASS');
