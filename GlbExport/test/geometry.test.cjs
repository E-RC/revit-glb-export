'use strict';
// node --test  (desde la carpeta GlbExport). Sin dependencias: solo geometria.cjs.
const test = require('node:test');
const assert = require('node:assert/strict');
const {windingStats, alignWinding, isInconsistent, twoSidedCats, splitByCell, mergeGeometry} = require('../src/geometry.cjs');

// Un triangulo en el plano y=0 con normal de vertice hacia +y; sentido antihorario visto desde +y es (0,0,0),(0,0,-1),(1,0,0)?
// Se arma con la regla de la mano derecha: (b-a) x (c-a) apunta a +y para a=(0,0,0), b=(0,0,1), c=(1,0,0).
const up = [0, 1, 0, 0, 1, 0, 0, 1, 0];
const tri = (a, b, c) => Float32Array.from([...a, ...b, ...c]);

test('windingStats: un triangulo con el orden acorde a la normal no cuenta como contrario', () => {
  const pos = tri([0, 0, 0], [0, 0, 1], [1, 0, 0]);
  const s = windingStats(pos, Float32Array.from(up), Uint32Array.from([0, 1, 2]));
  assert.deepEqual([s.tris, s.flipped], [1, 0]);
  assert.ok(Math.abs(s.area - 0.5) < 1e-9);
});

test('windingStats: el mismo triangulo con dos vertices intercambiados es contrario, y pesa por area', () => {
  const pos = tri([0, 0, 0], [0, 0, 1], [1, 0, 0]);
  const s = windingStats(pos, Float32Array.from(up), Uint32Array.from([0, 2, 1]));
  assert.deepEqual([s.tris, s.flipped], [1, 1]);
  assert.ok(Math.abs(s.flippedArea - 0.5) < 1e-9);
});

test('alignWinding da vuelta solo los contrarios, en el lugar, y no toca el resto', () => {
  const pos = Float32Array.from([0, 0, 0, 0, 0, 1, 1, 0, 0, 5, 0, 5, 5, 0, 6, 6, 0, 5]);
  const nor = Float32Array.from([...up, ...up]);
  const idx = Uint32Array.from([0, 1, 2, 3, 5, 4]);             // el primero esta bien, el segundo al reves
  const s = alignWinding(pos, nor, idx);
  assert.equal(s.flipped, 1);
  assert.deepEqual(Array.from(idx), [0, 1, 2, 3, 4, 5]);
  assert.equal(windingStats(pos, nor, idx).flipped, 0);          // idempotente
});

test('alignWinding ignora triangulos degenerados', () => {
  const pos = Float32Array.from([0, 0, 0, 1, 0, 0, 2, 0, 0]);
  const idx = Uint32Array.from([0, 1, 2]);
  const s = alignWinding(pos, Float32Array.from(up), idx);
  assert.equal(s.flipped, 0);
  assert.deepEqual(Array.from(idx), [0, 1, 2]);
});

test('isInconsistent: marca la categoria si hay muchos triangulos o area contraria, y tolera ruido', () => {
  assert.equal(isInconsistent({tris: 1000, flipped: 0, area: 100, flippedArea: 0}), false);
  assert.equal(isInconsistent({tris: 1000, flipped: 6, area: 100, flippedArea: 0.001}), false);     // 0,6 % y casi sin area
  assert.equal(isInconsistent({tris: 1000, flipped: 230, area: 100, flippedArea: 0.1}), true);      // 23 % de los triangulos
  assert.equal(isInconsistent({tris: 1000, flipped: 20, area: 100, flippedArea: 10}), true);        // 10 % del area
  assert.equal(isInconsistent({tris: 0, flipped: 0, area: 0, flippedArea: 0}), false);
  assert.equal(isInconsistent(undefined), false);
});

test('twoSidedCats une la lista pedida con las inconsistentes, y la deteccion se puede apagar', () => {
  const winding = new Map([['Walls', {tris: 1000, flipped: 0, area: 100, flippedArea: 0}], ['Railings', {tris: 1000, flipped: 230, area: 100, flippedArea: 10}]]);
  assert.deepEqual([...twoSidedCats({doubleSided: ['Topography']}, winding)].sort(), ['Railings', 'Topography']);
  assert.deepEqual([...twoSidedCats({doubleSided: ['Topography'], autoDoubleSided: false}, winding)], ['Topography']);
  assert.equal(twoSidedCats({}, new Map()).size, 0);
});

// dos cuadrados (4 triangulos): uno en la celda (0,0) y otro en la (1,0) con celdas de 10
const quadAt = (x, z) => [[x, 0, z], [x, 0, z + 1], [x + 1, 0, z + 1], [x + 1, 0, z]];
function twoQuads() {
  const verts = [...quadAt(2, 2), ...quadAt(12, 3)];
  const pos = Float32Array.from(verts.flat());
  const nor = Float32Array.from(verts.flatMap(() => [0, 1, 0]));
  const idx = Uint32Array.from([0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
  return {pos, nor, idx};
}

test('splitByCell reparte los triangulos por el centro y compacta los vertices de cada celda', () => {
  const {pos, nor, idx} = twoQuads();
  const parts = splitByCell(pos, nor, idx, 10, {x: 0, z: 0});
  assert.equal(parts.size, 2);
  const a = parts.get('0_0'), b = parts.get('1_0');
  assert.deepEqual([a.idx.length / 3, b.idx.length / 3], [2, 2]);
  assert.deepEqual([a.pos.length / 3, b.pos.length / 3], [4, 4]);
  assert.ok(Math.max(...a.idx) < 4 && Math.max(...b.idx) < 4);
  assert.deepEqual([a.ix, a.iz, b.ix, b.iz], [0, 0, 1, 0]);
});

test('splitByCell respeta el origen y las coordenadas negativas', () => {
  const {pos, nor, idx} = twoQuads();
  const parts = splitByCell(pos, nor, idx, 10, {x: 20, z: 0});     // el origen queda a la derecha: celdas -2 y -1
  assert.deepEqual([...parts.keys()].sort(), ['-1_0', '-2_0']);
});

test('splitByCell conserva el numero de triangulos y su geometria', () => {
  const {pos, nor, idx} = twoQuads();
  const parts = splitByCell(pos, nor, idx, 3, {x: 0, z: 0});
  let tris = 0, area = 0;
  for (const p of parts.values()) { tris += p.idx.length / 3; area += windingStats(p.pos, p.nor, p.idx).area; }
  assert.equal(tris, 4);
  assert.ok(Math.abs(area - 2) < 1e-6);
});

test('el color por vertice viaja con splitByCell y mergeGeometry, y solo sale si todas las partes lo traen', () => {
  const {pos, nor, idx} = twoQuads();
  const col = Float32Array.from(Array.from({length: 8}, (_, v) => [v, v * 2, v * 3]).flat());
  const parts = [...splitByCell(pos, nor, idx, 10, {x: 0, z: 0}, col).values()];
  const m = mergeGeometry(parts);
  assert.equal(m.col.length, 24);
  for (let i = 0; i < m.pos.length / 3; i++) {                    // cada vertice conserva el color del vertice original de igual posicion
    const v = [...Array(8).keys()].find((k) => pos[k * 3] === m.pos[i * 3] && pos[k * 3 + 2] === m.pos[i * 3 + 2]);
    assert.deepEqual(Array.from(m.col.slice(i * 3, i * 3 + 3)), [v, v * 2, v * 3]);
  }
  assert.equal(mergeGeometry([parts[0], {...parts[1], col: undefined}]).col, undefined);
});

test('mergeGeometry une partes desplazando los indices', () => {
  const {pos, nor, idx} = twoQuads();
  const parts = [...splitByCell(pos, nor, idx, 10, {x: 0, z: 0}).values()];
  const m = mergeGeometry(parts);
  assert.equal(m.pos.length, 24); assert.equal(m.nor.length, 24); assert.equal(m.idx.length, 12);
  assert.ok(Math.max(...m.idx) === 7 && Math.min(...m.idx) === 0);
});
