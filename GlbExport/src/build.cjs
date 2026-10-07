'use strict';
// Convierte scene.json + data.bin (salida de exporter.py) a GLB.
//   node build.cjs <carpetaExport> <salida.glb> [--rules reglas.json]
// Dependencias en %LOCALAPPDATA%\ArchiGlb\node_modules (las instala install.ps1).
const fs = require('fs');
const path = require('path');

const DEPS = path.join(process.env.LOCALAPPDATA || '', 'ArchiGlb');
const load = (m) => require(require.resolve(m, {paths: [DEPS]}));
const {Document, NodeIO} = load('@gltf-transform/core');
const {EXTMeshGPUInstancing, EXTMeshoptCompression} = load('@gltf-transform/extensions');
const {weld, reorder, join} = load('@gltf-transform/functions');
const {MeshoptEncoder, MeshoptSimplifier} = load('meshoptimizer');
const {windingStats, alignWinding, twoSidedCats, splitByCell, mergeGeometry} = require('./geometry.cjs');

const FT = 0.3048;                       // pies a metros
const srgb = (v) => Math.pow(v / 255, 2.2);

// Revit (x, y, z arriba) a glTF (x, z, -y), giro propio: conserva el sentido de las caras.
const toGl = (a, k) => {
  const o = new Float32Array(a.length);
  for (let i = 0; i < a.length; i += 3) { o[i] = a[i] * k; o[i + 1] = a[i + 2] * k; o[i + 2] = -a[i + 1] * k; }
  return o;
};

function parseArgs(argv) {
  const [dir, out, ...rest] = argv;
  if (!dir || !out) throw new Error('uso: node build.cjs <carpetaExport> <salida.glb> [--rules reglas.json]');
  const i = rest.indexOf('--rules');
  return {dir, out, rules: i >= 0 ? JSON.parse(fs.readFileSync(rest[i + 1], 'utf8')) : {}};
}

// reglas: {minTris, byCategory:{Cat:{ratio, errorM, lockBorder}}}
function ruleFor(rules, cat) {
  return (rules.byCategory && rules.byCategory[cat]) || null;
}

// Simplifica una primitiva ignorando las normales: Revit entrega cada cara con sus propias
// normales, asi que soldando con ellas cada cara queda aislada y el simplificador casi no
// puede colapsar nada. Se suelda solo por posicion, se simplifica y se recalculan normales
// (suaves dentro de creaseDeg, duras mas alla), dejando vertices separados en las aristas.
// minTris se compara con el total de la pieza (todas sus primitivas): una puerta se parte por
// material en primitivas chicas, pero la pieza completa si es pesada.
function simplifyPrim(prim, rule, groupTris, minTris, floorTris, creaseDeg) {
  const idxA = prim.getIndices().getArray();
  if (!rule || rule.ratio >= 1 || groupTris < minTris) return;
  const posA = prim.getAttribute('POSITION').getArray();
  const n = idxA.length / 3;

  const remap = new Map(), cpos = [], cidx = new Uint32Array(idxA.length), vmap = new Uint32Array(posA.length / 3);
  for (let v = 0; v < vmap.length; v++) {
    const k = `${Math.round(posA[v * 3] * 1e4)},${Math.round(posA[v * 3 + 1] * 1e4)},${Math.round(posA[v * 3 + 2] * 1e4)}`;
    let id = remap.get(k);
    if (id === undefined) { id = cpos.length / 3; remap.set(k, id); cpos.push(posA[v * 3], posA[v * 3 + 1], posA[v * 3 + 2]); }
    vmap[v] = id;
  }
  for (let i = 0; i < idxA.length; i++) cidx[i] = vmap[idxA[i]];

  const target = Math.max(Math.min(n, floorTris), Math.floor(n * rule.ratio)) * 3;
  const flags = ['ErrorAbsolute'];
  if (rule.lockBorder) flags.push('LockBorder');
  const [out] = MeshoptSimplifier.simplify(cidx, new Float32Array(cpos), 3, target, rule.errorM ?? 0.02, flags);

  // normales por triangulo (ponderadas por area) y suavizado con angulo de pliegue
  const P = cpos, T = out.length / 3, tn = new Float32Array(T * 3), ta = new Float32Array(T);
  const keep = [];
  for (let t = 0; t < T; t++) {
    const a0 = out[t * 3] * 3, b0 = out[t * 3 + 1] * 3, c0 = out[t * 3 + 2] * 3;
    const ux = P[b0] - P[a0], uy = P[b0 + 1] - P[a0 + 1], uz = P[b0 + 2] - P[a0 + 2];
    const vx = P[c0] - P[a0], vy = P[c0 + 1] - P[a0 + 1], vz = P[c0 + 2] - P[a0 + 2];
    const x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx, l = Math.hypot(x, y, z);
    if (l < 1e-12) continue;                       // triangulo degenerado
    tn[t * 3] = x / l; tn[t * 3 + 1] = y / l; tn[t * 3 + 2] = z / l; ta[t] = l; keep.push(t);
  }
  const vc = cpos.length / 3, start = new Uint32Array(vc + 1);
  for (const t of keep) for (let k = 0; k < 3; k++) start[out[t * 3 + k] + 1]++;
  for (let v = 0; v < vc; v++) start[v + 1] += start[v];
  const fill = start.slice(0, vc), inc = new Uint32Array(start[vc]);
  for (const t of keep) for (let k = 0; k < 3; k++) inc[fill[out[t * 3 + k]]++] = t;

  const cosC = Math.cos(((creaseDeg ?? 35) * Math.PI) / 180);
  const np = new Float32Array(keep.length * 9), nn = new Float32Array(keep.length * 9), ni = new Uint32Array(keep.length * 3);
  keep.forEach((t, i) => {
    for (let k = 0; k < 3; k++) {
      const v = out[t * 3 + k];
      let sx = 0, sy = 0, sz = 0;
      for (let j = start[v]; j < start[v + 1]; j++) {
        const u = inc[j];
        if (tn[u * 3] * tn[t * 3] + tn[u * 3 + 1] * tn[t * 3 + 1] + tn[u * 3 + 2] * tn[t * 3 + 2] >= cosC) {
          sx += tn[u * 3] * ta[u]; sy += tn[u * 3 + 1] * ta[u]; sz += tn[u * 3 + 2] * ta[u];
        }
      }
      const l = Math.hypot(sx, sy, sz) || 1;
      const o = (i * 3 + k) * 3;
      np[o] = P[v * 3]; np[o + 1] = P[v * 3 + 1]; np[o + 2] = P[v * 3 + 2];
      nn[o] = sx / l; nn[o + 1] = sy / l; nn[o + 2] = sz / l;
      ni[i * 3 + k] = i * 3 + k;
    }
  });
  prim.getAttribute('POSITION').setArray(np);
  prim.getAttribute('NORMAL').setArray(nn);
  prim.getIndices().setArray(ni);
}

// Rotacion + escala + traslacion (glTF) a partir de la matriz 3x4 de Revit.
function trs(m, mirrored) {
  let r = [[m[0], m[1], m[2]], [m[4], m[5], m[6]], [m[8], m[9], m[10]]];
  if (mirrored) r = r.map((row) => [-row[0], row[1], row[2]]);   // R * diag(-1,1,1)
  // C R C^-1 con C: (x,y,z) -> (x, z, -y)
  const C = [[1, 0, 0], [0, 0, 1], [0, -1, 0]];
  const mul = (a, b) => a.map((_, i) => b[0].map((__, j) => a[i][0] * b[0][j] + a[i][1] * b[1][j] + a[i][2] * b[2][j]));
  const tr = (a) => a[0].map((_, j) => a.map((row) => row[j]));
  const R = mul(mul(C, r), tr(C));
  const sc = [0, 1, 2].map((j) => Math.hypot(R[0][j], R[1][j], R[2][j]) || 1);
  const n = R.map((row) => row.map((v, j) => v / sc[j]));
  const t = n[0][0] + n[1][1] + n[2][2];
  let q;
  if (t > 0) { const s = Math.sqrt(t + 1) * 2; q = [(n[2][1] - n[1][2]) / s, (n[0][2] - n[2][0]) / s, (n[1][0] - n[0][1]) / s, s / 4]; }
  else if (n[0][0] > n[1][1] && n[0][0] > n[2][2]) { const s = Math.sqrt(1 + n[0][0] - n[1][1] - n[2][2]) * 2; q = [s / 4, (n[0][1] + n[1][0]) / s, (n[0][2] + n[2][0]) / s, (n[2][1] - n[1][2]) / s]; }
  else if (n[1][1] > n[2][2]) { const s = Math.sqrt(1 + n[1][1] - n[0][0] - n[2][2]) * 2; q = [(n[0][1] + n[1][0]) / s, s / 4, (n[1][2] + n[2][1]) / s, (n[0][2] - n[2][0]) / s]; }
  else { const s = Math.sqrt(1 + n[2][2] - n[0][0] - n[1][1]) * 2; q = [(n[0][2] + n[2][0]) / s, (n[1][2] + n[2][1]) / s, s / 4, (n[1][0] - n[0][1]) / s]; }
  // el sentido de la traslacion usa C y la escala a metros
  const T = [m[3] * FT, m[11] * FT, -m[7] * FT];
  return {t: T, q, s: sc};
}

// Parte la geometria estatica (la que no es instanciada) en mallas por celda de cellM metros, con un nombre
// `${categoria o Estatico}_${ix}_${iz}`: cada una tiene su propia caja y el motor descarta las que quedan
// fuera de la camara. Dentro de cada celda se funde por material. Devuelve cuantas celdas quedaron.
function chunkStatics(doc, buffer, root, cellM) {
  const acc = (type, arr) => doc.createAccessor().setType(type).setArray(arr).setBuffer(buffer);
  const nodes = root.listChildren().filter((n) => n.getMesh() && !n.getExtension('EXT_mesh_gpu_instancing'));
  const origin = {x: Infinity, z: Infinity};
  for (const n of nodes) for (const p of n.getMesh().listPrimitives()) {
    const lo = p.getAttribute('POSITION').getMin([]);
    origin.x = Math.min(origin.x, lo[0]); origin.z = Math.min(origin.z, lo[2]);
  }

  const cells = new Map();                           // nombre de celda -> Map(material -> partes)
  for (const n of nodes) {
    const mesh = n.getMesh(), base = mesh.getName() || 'Estatico';
    for (const p of mesh.listPrimitives()) {
      const color = p.getAttribute('COLOR_0');
      const parts = splitByCell(p.getAttribute('POSITION').getArray(), p.getAttribute('NORMAL').getArray(), p.getIndices().getArray(), cellM, origin, color && color.getArray());
      for (const part of parts.values()) {
        const name = `${base}_${part.ix}_${part.iz}`;
        if (!cells.has(name)) cells.set(name, new Map());
        const byMaterial = cells.get(name), mat = p.getMaterial();
        if (!byMaterial.has(mat)) byMaterial.set(mat, []);
        byMaterial.get(mat).push(part);
      }
      for (const a of [p.getAttribute('POSITION'), p.getAttribute('NORMAL'), color, p.getIndices()]) if (a) a.dispose();
      p.dispose();
    }
    root.removeChild(n); n.dispose(); mesh.dispose();
  }

  for (const [name, byMaterial] of cells) {
    const mesh = doc.createMesh(name);
    for (const [mat, parts] of byMaterial) {
      const g = mergeGeometry(parts);
      const prim = doc.createPrimitive().setAttribute('POSITION', acc('VEC3', g.pos)).setAttribute('NORMAL', acc('VEC3', g.nor))
        .setIndices(acc('SCALAR', g.idx)).setMaterial(mat);
      if (g.col) prim.setAttribute('COLOR_0', acc('VEC3', g.col));
      mesh.addPrimitive(prim);
    }
    root.addChild(doc.createNode(name).setMesh(mesh));
  }
  return cells.size;
}

async function main() {
  const {dir, out, rules} = parseArgs(process.argv.slice(2));
  const scene = JSON.parse(fs.readFileSync(path.join(dir, 'scene.json'), 'utf8'));
  const bin = fs.readFileSync(path.join(dir, 'data.bin'));
  const ab = bin.buffer.slice(bin.byteOffset, bin.byteOffset + bin.byteLength);
  const minTris = rules.minTris ?? 300;

  const doc = new Document();
  const buffer = doc.createBuffer();
  const ext = doc.createExtension(EXTMeshGPUInstancing).setRequired(true);
  const root = doc.createScene('forma-vr');

  // Materiales con el mismo color y transparencia se funden (cada uno distinto cuesta un draw call).
  // Cada uno se crea a una cara (la GPU descarta las traseras) y, donde hace falta, tambien a doble cara.
  const matDefs = [], canon = [], matByKey = new Map();
  scene.materials.forEach((m, i) => {
    const key = `${m.rgb.join(',')}|${m.alpha.toFixed(3)}`;
    if (!matByKey.has(key)) { matByKey.set(key, matDefs.length); matDefs.push({name: m.name || `mat${i}`, rgb: m.rgb, alpha: m.alpha}); }
    canon[i] = matByKey.get(key);
  });
  const allTwoSided = rules.doubleSidedAll === true;      // comportamiento anterior: todo a doble cara
  const matCache = new Map(), matIndex = new Map();
  const matOf = (i, two) => {
    const d = matDefs[i], both = two || d.alpha < 1;        // lo transparente va siempre a doble cara
    const key = `${i}|${both ? 2 : 1}`;
    let mat = matCache.get(key);
    if (!mat) {
      mat = doc.createMaterial(both && d.alpha >= 1 ? `${d.name} (2 caras)` : d.name)
        .setBaseColorFactor([srgb(d.rgb[0]), srgb(d.rgb[1]), srgb(d.rgb[2]), d.alpha])
        .setMetallicFactor(0).setRoughnessFactor(0.85).setDoubleSided(both)
        .setAlphaMode(d.alpha < 1 ? 'BLEND' : 'OPAQUE');
      matCache.set(key, mat); matIndex.set(mat, i);
    }
    return mat;
  };

  const acc = (type, arr) => doc.createAccessor().setType(type).setArray(arr).setBuffer(buffer);

  await MeshoptEncoder.ready;
  await MeshoptSimplifier.ready;

  const f = (off, n, T) => new T(ab.slice(off, off + n * T.BYTES_PER_ELEMENT));
  const rules_ = [];                              // [prim, cat, grupo]: se simplifica despues de soldar
  const groupSum = new Map();

  // Primitiva GL desde datos en ejes Revit; mirrored = espejo en X local y giro de caras.
  const makePrim = (pos, nor, idx, mat, mirrored) => {
    if (mirrored) {
      for (let i = 0; i < pos.length; i += 3) { pos[i] = -pos[i]; nor[i] = -nor[i]; }
      for (let i = 0; i < idx.length; i += 3) { const t = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t; }
    }
    return doc.createPrimitive()
      .setAttribute('POSITION', acc('VEC3', toGl(pos, FT)))
      .setAttribute('NORMAL', acc('VEC3', toGl(nor, 1)))
      .setIndices(acc('SCALAR', idx))
      .setMaterial(matOf(mat, allTwoSided));
  };

  // Datos de un registro de Revit con las caras giradas segun sus normales (alignWinding=false las deja como vienen).
  // El conteo de caras contrarias por categoria decide mas abajo cuales quedan a doble cara.
  const windingByCat = new Map();
  const fixWinding = rules.alignWinding === false ? windingStats : alignWinding;
  const recData = (r, c) => {
    const d = [new Float32Array(f(r.pos, r.nv * 3, Float32Array)), new Float32Array(f(r.nor, r.nv * 3, Float32Array)), new Uint32Array(f(r.idx, r.nt * 3, Uint32Array))];
    const s = fixWinding(d[0], d[1], d[2]), w = windingByCat.get(c) || {tris: 0, flipped: 0, area: 0, flippedArea: 0};
    for (const k of Object.keys(w)) w[k] += s[k];
    windingByCat.set(c, w);
    return d;
  };

  // Acumulador por (categoria, material) para estaticos y definiciones de una sola instancia.
  const acc2 = new Map();
  const addStatic = (cat, mat, pos, nor, idx, m) => {
    const k = `${cat}\u0000${mat}`;
    let a = acc2.get(k);
    if (!a) { a = {cat, mat, pos: [], nor: [], idx: [], nv: 0}; acc2.set(k, a); }
    const P = new Float32Array(pos.length), N = new Float32Array(nor.length);
    for (let i = 0; i < pos.length; i += 3) {
      const x = pos[i], y = pos[i + 1], z = pos[i + 2];
      if (m) {
        P[i] = m[0] * x + m[1] * y + m[2] * z + m[3]; P[i + 1] = m[4] * x + m[5] * y + m[6] * z + m[7]; P[i + 2] = m[8] * x + m[9] * y + m[10] * z + m[11];
        const nx = nor[i], ny = nor[i + 1], nz = nor[i + 2];
        N[i] = m[0] * nx + m[1] * ny + m[2] * nz; N[i + 1] = m[4] * nx + m[5] * ny + m[6] * nz; N[i + 2] = m[8] * nx + m[9] * ny + m[10] * nz;
      } else { P[i] = x; P[i + 1] = y; P[i + 2] = z; N[i] = nor[i]; N[i + 1] = nor[i + 1]; N[i + 2] = nor[i + 2]; }
    }
    const I = new Uint32Array(idx.length);
    const flip = m && (m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8])) < 0;
    for (let i = 0; i < idx.length; i += 3) {
      I[i] = idx[i] + a.nv; I[i + 1] = idx[i + (flip ? 2 : 1)] + a.nv; I[i + 2] = idx[i + (flip ? 1 : 2)] + a.nv;
    }
    a.pos.push(P); a.nor.push(N); a.idx.push(I); a.nv += pos.length / 3;
  };
  const cat = (arrs, T) => { const n = arrs.reduce((s, a) => s + a.length, 0); const o = new T(n); let p = 0; for (const a of arrs) { o.set(a, p); p += a.length; } return o; };

  const mergeRecs = (recs, c) => {
    if (recs.length === 1) return recData(recs[0], c);
    let nv = 0; const P = [], N = [], I = [];
    for (const r of recs) { const [p, n, i] = recData(r, c); P.push(p); N.push(n); I.push(i.map((v) => v + nv)); nv += p.length / 3; }
    return [cat(P, Float32Array), cat(N, Float32Array), cat(I, Uint32Array)];
  };
  const byMat = (prims) => { const g = new Map(); for (const r of prims) { const k = canon[r.mat]; if (!g.has(k)) g.set(k, []); g.get(k).push(r); } return [...g]; };

  const staticByCat = new Map();
  for (const r of scene.statics) { if (!staticByCat.has(r.cat)) staticByCat.set(r.cat, []); staticByCat.get(r.cat).push(r); }
  for (const [c, recs] of staticByCat) for (const [mat, rs] of byMat(recs)) { const [p, n, i] = mergeRecs(rs, c); addStatic(c, mat, p, n, i, null); }

  // Definiciones: varias instancias o espejadas con varias -> instanciadas; una sola -> estatico.
  const groups = new Map();
  for (const i of scene.insts) {
    const k = `${i.def}|${i.mirror ? 1 : 0}`;
    if (!groups.has(k)) groups.set(k, []);
    groups.get(k).push(i);
  }
  const minInst = rules.minInstances ?? 2;
  const mergeStatics = rules.mergeStatics !== false, keepSep = new Set(rules.keepSeparate || []);
  let instNodes = 0;
  for (const [k, list] of groups) {
    const [defId, mirror] = k.split('|');
    const def = scene.defs[+defId];
    if (list.length < minInst) {
      for (const inst of list) for (const [mat, rs] of byMat(def.prims)) { const [p, n, i] = mergeRecs(rs, def.cat); addStatic(def.cat, mat, p, n, i, inst.m); }
      continue;
    }
    const mesh = doc.createMesh(`${def.cat} #${def.id}${mirror === '1' ? 'm' : ''}`);
    for (const [mat, rs] of byMat(def.prims)) { const [p, n, i] = mergeRecs(rs, def.cat); const prim = makePrim(p, n, i, mat, mirror === '1'); mesh.addPrimitive(prim); rules_.push([prim, def.cat, mesh]); }
    const node = doc.createNode(mesh.getName()).setMesh(mesh);
    const T = new Float32Array(list.length * 3), Q = new Float32Array(list.length * 4), S = new Float32Array(list.length * 3);
    list.forEach((inst, n) => { const v = trs(inst.m, mirror === '1'); T.set(v.t, n * 3); Q.set(v.q, n * 4); S.set(v.s, n * 3); });
    node.setExtension('EXT_mesh_gpu_instancing', ext.createInstancedMesh()
      .setAttribute('TRANSLATION', acc('VEC3', T)).setAttribute('ROTATION', acc('VEC4', Q)).setAttribute('SCALE', acc('VEC3', S)));
    root.addChild(node);
    instNodes++;
  }

  const meshByCat = new Map();
  for (const a of acc2.values()) {
    let mesh = meshByCat.get(a.cat);
    if (!mesh) {
      const nm = mergeStatics && !keepSep.has(a.cat) ? '' : a.cat;       // sin nombre = se fusiona despues
      mesh = doc.createMesh(nm); meshByCat.set(a.cat, mesh); root.addChild(doc.createNode(nm).setMesh(mesh));
    }
    const prim = makePrim(cat(a.pos, Float32Array), cat(a.nor, Float32Array), cat(a.idx, Uint32Array), a.mat, false);
    mesh.addPrimitive(prim); rules_.push([prim, a.cat, prim]);
  }

  // Una cara (descarta las traseras) salvo en categorias abiertas o con datos de Revit inconsistentes.
  const twoSided = twoSidedCats(rules, windingByCat);
  for (const [prim, c] of rules_) if (twoSided.has(c)) prim.setMaterial(matOf(matIndex.get(prim.getMaterial()), true));

  // Color en los vertices: todos los materiales opacos pasan a uno solo (blanco, a una o dos caras) y su color va en
  // COLOR_0 (RGB, sin alfa: con alfa el motor lo trata como transparente). Con eso join funde por completo cada
  // categoria o celda: menos draw calls con los mismos pixeles. Lo transparente conserva su material.
  const bakeColors = () => {
    const neutral = new Map();
    for (const [prim] of rules_) {
      const mat = prim.getMaterial();
      if (mat.getAlphaMode() !== 'OPAQUE') continue;
      const two = mat.getDoubleSided(), [r, g, b] = matDefs[matIndex.get(mat)].rgb.map(srgb), n = prim.getAttribute('POSITION').getCount();
      if (!neutral.has(two)) neutral.set(two, doc.createMaterial(two ? 'color (2 caras)' : 'color').setBaseColorFactor([1, 1, 1, 1]).setMetallicFactor(0).setRoughnessFactor(0.85).setDoubleSided(two));
      const col = new Float32Array(n * 3);
      for (let v = 0; v < n; v++) { col[v * 3] = r; col[v * 3 + 1] = g; col[v * 3 + 2] = b; }
      prim.setAttribute('COLOR_0', acc('VEC3', col)).setMaterial(neutral.get(two));
    }
  };

  await doc.transform(weld({tolerance: 1e-4}));
  for (const [prim, , g] of rules_) groupSum.set(g, (groupSum.get(g) || 0) + prim.getIndices().getCount() / 3);
  for (const [prim, c, g] of rules_) simplifyPrim(prim, ruleFor(rules, c), groupSum.get(g), minTris, rules.minPrimTris ?? 8, rules.creaseDeg);
  if (rules.vertexColors) bakeColors();
  if (mergeStatics) await doc.transform(join({keepMeshes: false, keepNamed: true}));
  const cells = rules.cellM > 0 ? chunkStatics(doc, buffer, root, rules.cellM) : 0;

  // Recentrar en XZ sobre la geometria estatica (las coordenadas de Revit llegan lejos del origen).
  if (rules.recenter !== false) {
    const mn = [Infinity, Infinity], mx = [-Infinity, -Infinity];
    for (const n of root.listChildren()) {
      if (n.getExtension('EXT_mesh_gpu_instancing') || !n.getMesh()) continue;
      for (const p of n.getMesh().listPrimitives()) {
        const a = p.getAttribute('POSITION');
        const lo = a.getMin([]), hi = a.getMax([]);
        mn[0] = Math.min(mn[0], lo[0]); mn[1] = Math.min(mn[1], lo[2]); mx[0] = Math.max(mx[0], hi[0]); mx[1] = Math.max(mx[1], hi[2]);
      }
    }
    const model = doc.createNode('model').setTranslation([-(mn[0] + mx[0]) / 2, 0, -(mn[1] + mx[1]) / 2]);
    for (const c of root.listChildren()) { root.removeChild(c); model.addChild(c); }
    root.addChild(model);
  }
  await doc.transform(weld({tolerance: 1e-4}));   // las primitivas simplificadas quedan sin soldar
  await doc.transform(reorder({encoder: MeshoptEncoder, target: 'size'}));
  doc.createExtension(EXTMeshoptCompression).setRequired(true)
    .setEncoderOptions({method: EXTMeshoptCompression.EncoderMethod.QUANTIZE});

  const io = new NodeIO().registerExtensions([EXTMeshGPUInstancing, EXTMeshoptCompression])
    .registerDependencies({'meshopt.encoder': MeshoptEncoder});
  await io.write(out, doc);

  let tris = 0, prims = 0, statPrims = 0;
  const byCat = {}, hist = {};
  for (const n of doc.getRoot().listNodes()) {
    const m = n.getMesh(); if (!m) continue;
    const inst = n.getExtension('EXT_mesh_gpu_instancing');
    const cnt = inst ? inst.listAttributes()[0].getCount() : 1;
    const name = m.getName().replace(/ #\d+m?$/, '').replace(/_-?\d+_-?\d+$/, '') || 'Estatico';
    if (inst) { const k = cnt < 3 ? '2' : cnt < 6 ? '3-5' : cnt < 11 ? '6-10' : cnt < 31 ? '11-30' : '31+'; hist[k] = (hist[k] || 0) + m.listPrimitives().length; }
    for (const p of m.listPrimitives()) {
      if (!inst) statPrims++;
      const t = (p.getIndices().getCount() / 3) * cnt;
      tris += t; prims++;
      byCat[name] = (byCat[name] || 0) + t;
    }
  }
  const top = Object.entries(byCat).sort((x, y) => y[1] - x[1]).slice(0, 12).map(([k, v]) => `${k}: ${Math.round(v)}`);
  const wind = [...windingByCat].filter(([, s]) => s.flipped > 0).sort((x, y) => y[1].flipped - x[1].flipped).slice(0, 6)
    .map(([c, s]) => `${c}: ${(100 * s.flipped / s.tris).toFixed(1)} % tris, ${(100 * s.flippedArea / s.area).toFixed(1)} % area${twoSided.has(c) ? ', 2 caras' : ''}`);
  console.log(JSON.stringify({out, mb: +(fs.statSync(out).size / 1048576).toFixed(2), instancedMeshes: instNodes, drawCalls: prims, staticPrims: statPrims, cells, twoSided: [...twoSided], winding: wind, instancedPrimsByCount: hist, trianglesRendered: Math.round(tris), top}));
}

main().catch((e) => { console.error(e); process.exit(1); });
