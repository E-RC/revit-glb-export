'use strict';
// Funciones puras de geometria para build.cjs (sin dependencias, probadas en test/geometry.test.cjs).

const EPS = 1e-12;

// Recorre los triangulos y cuenta los que giran al reves de la normal de vertice. Con fix=true los corrige
// intercambiando dos indices (lugar, sin tocar vertices ni normales).
function scan(pos, nor, idx, fix) {
  const s = {tris: idx.length / 3, flipped: 0, area: 0, flippedArea: 0};
  for (let t = 0; t < idx.length; t += 3) {
    const a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
    const ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
    const vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
    const gx = uy * vz - uz * vy, gy = uz * vx - ux * vz, gz = ux * vy - uy * vx;
    const area = Math.hypot(gx, gy, gz) / 2;
    const d = gx * (nor[a] + nor[b] + nor[c]) + gy * (nor[a + 1] + nor[b + 1] + nor[c + 1]) + gz * (nor[a + 2] + nor[b + 2] + nor[c + 2]);
    s.area += area;
    if (d < -EPS) {
      s.flipped++; s.flippedArea += area;
      if (fix) { const tmp = idx[t + 1]; idx[t + 1] = idx[t + 2]; idx[t + 2] = tmp; }
    }
  }
  return s;
}

const windingStats = (pos, nor, idx) => scan(pos, nor, idx, false);
const alignWinding = (pos, nor, idx) => scan(pos, nor, idx, true);

// Una categoria con muchos triangulos (>5 %) o area (>0,5 %) en contra: los datos de Revit no son confiables
// para descartar la cara trasera, asi que se deja a doble cara.
function isInconsistent(s) {
  if (!s || !s.tris) return false;
  return s.flipped / s.tris > 0.05 || (s.area > 0 && s.flippedArea / s.area > 0.005);
}

// Categorias que se dejan a doble cara: las pedidas en rules.doubleSided y, salvo autoDoubleSided=false, las
// que salen inconsistentes. winding: Map categoria -> estadisticas de windingStats sumadas.
function twoSidedCats(rules, winding) {
  const set = new Set(rules.doubleSided || []);
  if (rules.autoDoubleSided !== false) for (const [cat, s] of winding) if (isInconsistent(s)) set.add(cat);
  return set;
}

// Reparte los triangulos en celdas de cellM metros (ejes x y z de glTF) segun su centro. Cada celda queda con
// sus propios vertices, compactados. origin: esquina de la celda (0,0). col: color por vertice (VEC3), opcional.
function splitByCell(pos, nor, idx, cellM, origin, col) {
  const cells = new Map();
  for (let t = 0; t < idx.length; t += 3) {
    const a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
    const cx = (pos[a] + pos[b] + pos[c]) / 3, cz = (pos[a + 2] + pos[b + 2] + pos[c + 2]) / 3;
    const ix = Math.floor((cx - origin.x) / cellM), iz = Math.floor((cz - origin.z) / cellM);
    const key = `${ix}_${iz}`;
    let cell = cells.get(key);
    if (!cell) { cell = {ix, iz, tris: []}; cells.set(key, cell); }
    cell.tris.push(t);
  }
  const remap = new Int32Array(pos.length / 3).fill(-1);
  const parts = new Map();
  for (const [key, cell] of cells) {
    const used = [], cidx = new Uint32Array(cell.tris.length * 3);
    cell.tris.forEach((t, n) => {
      for (let k = 0; k < 3; k++) {
        const v = idx[t + k];
        if (remap[v] < 0) { remap[v] = used.length; used.push(v); }
        cidx[n * 3 + k] = remap[v];
      }
    });
    const cpos = new Float32Array(used.length * 3), cnor = new Float32Array(used.length * 3), ccol = col ? new Float32Array(used.length * 3) : undefined;
    used.forEach((v, n) => {
      cpos.set(pos.subarray(v * 3, v * 3 + 3), n * 3); cnor.set(nor.subarray(v * 3, v * 3 + 3), n * 3);
      if (col) ccol.set(col.subarray(v * 3, v * 3 + 3), n * 3);
      remap[v] = -1;
    });
    parts.set(key, {ix: cell.ix, iz: cell.iz, pos: cpos, nor: cnor, col: ccol, idx: cidx});
  }
  return parts;
}

// Une partes {pos, nor, idx, col?} en una sola, desplazando los indices. El color solo sale si todas lo traen.
function mergeGeometry(parts) {
  const nv = parts.reduce((s, p) => s + p.pos.length / 3, 0), ni = parts.reduce((s, p) => s + p.idx.length, 0);
  const pos = new Float32Array(nv * 3), nor = new Float32Array(nv * 3), idx = new Uint32Array(ni);
  const col = parts.every((p) => p.col) ? new Float32Array(nv * 3) : undefined;
  let vo = 0, io = 0;
  for (const p of parts) {
    pos.set(p.pos, vo * 3); nor.set(p.nor, vo * 3);
    if (col) col.set(p.col, vo * 3);
    for (let i = 0; i < p.idx.length; i++) idx[io + i] = p.idx[i] + vo;
    vo += p.pos.length / 3; io += p.idx.length;
  }
  return {pos, nor, col, idx};
}

module.exports = {windingStats, alignWinding, isInconsistent, twoSidedCats, splitByCell, mergeGeometry};
