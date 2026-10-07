# -*- coding: utf-8 -*-
"""
Exportador de la vista 3D activa a un formato intermedio (scene.json + data.bin)
que GlbExport/ convierte a GLB. Corre en IronPython 2.7 (pyRevit), Revit 2024+.

Idea: CustomExporter entrega la vista tal como se ve (ocultos, filtros, caja de seccion).
Las instancias repetidas (puertas, ventanas...) se guardan UNA vez como definicion
mas una lista de transformaciones. La geometria directa (muros, pisos...) se junta por
categoria y material. Todo queda en pies y ejes de Revit; el paso a metros / Y arriba
lo hace el conversor Node.

Dos pasadas:
  1. Firma de cada instancia (conteo de caras y suma de coordenadas) para agruparlas.
  2. Extraccion real: la geometria se lee solo en la primera instancia de cada grupo y
     el resto se salta (RenderNodeAction.Skip).
"""
import io
import json
import time
from array import array
from Autodesk.Revit import DB

SCHEMA = 1
IDENT = (1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0)


def _tf(t):
    """DB.Transform -> 12 floats (3x4, filas), columnas = ejes de la base."""
    x, y, z, o = t.BasisX, t.BasisY, t.BasisZ, t.Origin
    return (x.X, y.X, z.X, o.X, x.Y, y.Y, z.Y, o.Y, x.Z, y.Z, z.Z, o.Z)


def _mul(a, b):
    """a * b (aplica b y luego a)."""
    return (
        a[0] * b[0] + a[1] * b[4] + a[2] * b[8],
        a[0] * b[1] + a[1] * b[5] + a[2] * b[9],
        a[0] * b[2] + a[1] * b[6] + a[2] * b[10],
        a[0] * b[3] + a[1] * b[7] + a[2] * b[11] + a[3],
        a[4] * b[0] + a[5] * b[4] + a[6] * b[8],
        a[4] * b[1] + a[5] * b[5] + a[6] * b[9],
        a[4] * b[2] + a[5] * b[6] + a[6] * b[10],
        a[4] * b[3] + a[5] * b[7] + a[6] * b[11] + a[7],
        a[8] * b[0] + a[9] * b[4] + a[10] * b[8],
        a[8] * b[1] + a[9] * b[5] + a[10] * b[9],
        a[8] * b[2] + a[9] * b[6] + a[10] * b[10],
        a[8] * b[3] + a[9] * b[7] + a[10] * b[11] + a[11],
    )


def _det(m):
    return (m[0] * (m[5] * m[10] - m[6] * m[9])
            - m[1] * (m[4] * m[10] - m[6] * m[8])
            + m[2] * (m[4] * m[9] - m[5] * m[8]))


def _eid(e):
    try:
        return e.Value            # Revit 2024+ (int64)
    except AttributeError:
        return e.IntegerValue


class _Base(DB.IExportContext):
    """Callbacks vacios y pila de transformaciones de vinculos."""

    def __init__(self, doc, exclude=None, include_links=True):
        self.docs = [doc]
        self.links = [IDENT]
        self.cancelled = False
        self.exclude = exclude or set()
        self.include_links = include_links
        self.muted = 0                # >0 dentro de un vinculo que no se incluye
        self.cat = "(sin categoria)"

    def Start(self):
        return True

    def Finish(self):
        pass

    def IsCanceled(self):
        return self.cancelled

    def OnViewBegin(self, node):
        return DB.RenderNodeAction.Proceed

    def OnViewEnd(self, eid):
        pass

    def OnElementBegin(self, eid):
        if self.muted:
            return DB.RenderNodeAction.Skip
        try:
            el = self.docs[-1].GetElement(eid)
            c = el.Category if el is not None else None
            self.cat = c.Name if c else "(sin categoria)"
        except Exception:
            self.cat = "(sin categoria)"
        if self.cat in self.exclude:
            return DB.RenderNodeAction.Skip
        return DB.RenderNodeAction.Proceed

    def OnElementEnd(self, eid):
        pass

    def OnLinkBegin(self, node):
        self.docs.append(node.GetDocument())
        self.links.append(_mul(self.links[-1], _tf(node.GetTransform())))
        if not self.include_links:
            self.muted += 1
        return DB.RenderNodeAction.Proceed

    def OnLinkEnd(self, node):
        self.docs.pop()
        self.links.pop()
        if not self.include_links:
            self.muted -= 1

    def OnFaceBegin(self, node):
        return DB.RenderNodeAction.Proceed

    def OnFaceEnd(self, node):
        pass

    def OnRPC(self, node):
        pass

    def OnLight(self, node):
        pass

    def OnMaterial(self, node):
        pass

    def OnInstanceBegin(self, node):
        return DB.RenderNodeAction.Proceed

    def OnInstanceEnd(self, node):
        pass

    def OnPolymesh(self, node):
        pass


class SignaturePass(_Base):
    """Pasada 1: una firma por instancia de primer nivel, en orden de aparicion."""

    def __init__(self, doc, exclude=None, include_links=True):
        _Base.__init__(self, doc, exclude, include_links)
        self.depth = 0
        self.cur = None
        self.sigs = []

    def OnInstanceBegin(self, node):
        if self.depth == 0:
            self.cur = []
        self.depth += 1
        return DB.RenderNodeAction.Proceed

    def OnInstanceEnd(self, node):
        self.depth -= 1
        if self.depth == 0:
            self.sigs.append(hash(tuple(self.cur)))
            self.cur = None

    def OnPolymesh(self, node):
        if self.cur is None:
            return
        pts = node.GetPoints()
        sx = sy = sz = 0.0
        for p in pts:
            sx += p.X
            sy += p.Y
            sz += p.Z
        self.cur.append((node.NumberOfFacets, node.NumberOfPoints,
                         round(sx, 4), round(sy, 4), round(sz, 4)))


class _Prim(object):
    __slots__ = ("mat", "pos", "nor", "idx", "nv")

    def __init__(self, mat):
        self.mat = mat
        self.pos = array("f")
        self.nor = array("f")
        self.idx = array("I")
        self.nv = 0


class ExtractPass(_Base):
    """Pasada 2: geometria. Definiciones (instanciadas) y estaticos por categoria."""

    def __init__(self, doc, groups, exclude=None, include_links=True):
        _Base.__init__(self, doc, exclude, include_links)
        self.groups = groups
        self.ix = -1                  # indice de instancia de primer nivel
        self.stack = []               # transformaciones relativas de instancias anidadas
        self.skip_pending = False
        self.mat = 0
        self.materials = []
        self.mat_ix = {}
        self.defs = []                # {"id","cat","prims":{mat:_Prim}}
        self.def_of = {}              # grupo -> indice en defs
        self.insts = []               # {"def","m","mirror"}
        self.rec = None               # definicion en grabacion
        self._in_inst = False
        self.statics = {}             # (cat, mat) -> _Prim
        self.mismatch = 0

    def OnMaterial(self, node):
        try:
            c = node.Color
            name = ""
            mid = node.MaterialId
            if mid is not None and _eid(mid) > 0:
                m = self.docs[-1].GetElement(mid)
                name = m.Name if m is not None else ""
            key = (c.Red, c.Green, c.Blue, round(node.Transparency, 3), name)
        except Exception:
            key = (178, 178, 178, 0.0, "")
        i = self.mat_ix.get(key)
        if i is None:
            i = self.mat_ix[key] = len(self.materials)
            self.materials.append({"name": key[4], "rgb": [int(key[0]), int(key[1]), int(key[2])], "alpha": float(1.0 - key[3])})
        self.mat = i

    def OnInstanceBegin(self, node):
        self.skip_pending = False     # un Skip previo sin OnInstanceEnd
        if not self._in_inst:
            self.ix += 1
            g = self.groups[self.ix]
            world = _mul(self.links[-1], _tf(node.GetTransform()))
            if g in self.def_of:
                self.insts.append({"def": self.def_of[g], "m": world, "mirror": _det(world) < 0})
                self.skip_pending = True
                return DB.RenderNodeAction.Skip
            d = {"id": len(self.defs), "cat": self.cat, "prims": {}}
            self.defs.append(d)
            self.def_of[g] = d["id"]
            self.insts.append({"def": d["id"], "m": world, "mirror": _det(world) < 0})
            self.rec = d
            self._in_inst = True
            self.stack = [IDENT]
        else:
            self.stack.append(_mul(self.stack[-1], _tf(node.GetTransform())))
        return DB.RenderNodeAction.Proceed

    def OnInstanceEnd(self, node):
        if self.skip_pending:
            self.skip_pending = False
            return
        if len(self.stack) > 1:
            self.stack.pop()
        else:
            self.stack = []
            self.rec = None
            self._in_inst = False

    def OnPolymesh(self, node):
        if self.rec is not None:
            prims, m = self.rec["prims"], self.stack[-1]
        else:
            prims, m = None, self.links[-1]
        if prims is not None:
            prim = prims.get(self.mat)
            if prim is None:
                prim = prims[self.mat] = _Prim(self.mat)
        else:
            key = (self.cat, self.mat)
            prim = self.statics.get(key)
            if prim is None:
                prim = self.statics[key] = _Prim(self.mat)
        self._append(prim, node, m)

    @staticmethod
    def _append(prim, node, m):
        pts = node.GetPoints()
        facets = node.GetFacets()
        n = len(pts)
        ident = m is IDENT
        flat = []
        for p in pts:
            x, y, z = p.X, p.Y, p.Z
            if ident:
                flat.extend((x, y, z))
            else:
                flat.extend((m[0] * x + m[1] * y + m[2] * z + m[3],
                             m[4] * x + m[5] * y + m[6] * z + m[7],
                             m[8] * x + m[9] * y + m[10] * z + m[11]))
        nrm = node.GetNormals()
        dist = node.DistributionOfNormals
        nn = len(nrm)

        def rot(v):
            if ident:
                return (v.X, v.Y, v.Z)
            return (m[0] * v.X + m[1] * v.Y + m[2] * v.Z,
                    m[4] * v.X + m[5] * v.Y + m[6] * v.Z,
                    m[8] * v.X + m[9] * v.Y + m[10] * v.Z)

        base = prim.nv
        if dist == DB.DistributionOfNormals.OnEachFacet and nn == len(facets):
            # una normal por faceta: se duplican vertices por faceta
            for k, f in enumerate(facets):
                nx, ny, nz = rot(nrm[k])
                for vi in (f.V1, f.V2, f.V3):
                    prim.pos.fromlist(flat[vi * 3:vi * 3 + 3])
                    prim.nor.fromlist([nx, ny, nz])
                prim.idx.fromlist([prim.nv, prim.nv + 1, prim.nv + 2])
                prim.nv += 3
            return
        if dist == DB.DistributionOfNormals.AtEachPoint and nn == n:
            nflat = []
            for v in nrm:
                nflat.extend(rot(v))
        else:  # OnePerFace (o inconsistente): la misma normal para todos los puntos
            nx, ny, nz = rot(nrm[0]) if nn else (0.0, 0.0, 1.0)
            nflat = [nx, ny, nz] * n
        prim.pos.fromlist(flat)
        prim.nor.fromlist(nflat)
        idx = []
        for f in facets:
            idx.extend((base + f.V1, base + f.V2, base + f.V3))
        prim.idx.fromlist(idx)
        prim.nv += n


def _dump(prim, blob, offset):
    rec = {"mat": prim.mat, "nv": prim.nv, "nt": len(prim.idx) // 3}
    for name, arr in (("pos", prim.pos), ("nor", prim.nor), ("idx", prim.idx)):
        data = arr.tostring()
        blob.write(data)
        rec[name] = offset
        offset += len(data)
    return rec, offset


def export_view(doc, view, outdir, log=None, exclude=None, include_links=True):
    """Exporta `view` (View3D) a outdir/scene.json + outdir/data.bin. Devuelve estadisticas."""
    import os
    if not isinstance(view, DB.View3D):
        raise ValueError("La vista activa no es una vista 3D")
    log = log or (lambda s: None)
    if not os.path.isdir(outdir):
        os.makedirs(outdir)

    t0 = time.time()
    p1 = SignaturePass(doc, exclude, include_links)
    ex = DB.CustomExporter(doc, p1)
    ex.IncludeGeometricObjects = False
    ex.ShouldStopOnError = False
    ex.Export(view)
    sig_ids = {}
    groups = [sig_ids.setdefault(s, len(sig_ids)) for s in p1.sigs]
    log("pasada 1: %d instancias, %d grupos, %.0f s" % (len(groups), len(sig_ids), time.time() - t0))

    t1 = time.time()
    p2 = ExtractPass(doc, groups, exclude, include_links)
    ex = DB.CustomExporter(doc, p2)
    ex.IncludeGeometricObjects = False
    ex.ShouldStopOnError = False
    ex.Export(view)
    if p2.ix + 1 != len(groups):
        raise RuntimeError("Las dos pasadas no coinciden (%d vs %d instancias)" % (p2.ix + 1, len(groups)))
    log("pasada 2: %.0f s" % (time.time() - t1))

    scene = {"schema": SCHEMA, "units": "ft", "up": "Z", "view": view.Name,
             "materials": p2.materials, "defs": [], "insts": [], "statics": []}
    offset = 0
    with io.open(os.path.join(outdir, "data.bin"), "wb") as blob:
        for d in p2.defs:
            prims = []
            for prim in d["prims"].values():
                rec, offset = _dump(prim, blob, offset)
                prims.append(rec)
            scene["defs"].append({"id": d["id"], "cat": d["cat"], "prims": prims})
        for (cat, _), prim in sorted(p2.statics.items(), key=lambda kv: kv[0]):
            rec, offset = _dump(prim, blob, offset)
            rec["cat"] = cat
            scene["statics"].append(rec)
    scene["insts"] = [{"def": i["def"], "m": [round(v, 6) for v in i["m"]], "mirror": i["mirror"]} for i in p2.insts]
    with io.open(os.path.join(outdir, "scene.json"), "w", encoding="utf-8") as f:
        f.write(json.dumps(scene, ensure_ascii=False))

    uses = {}
    for i in scene["insts"]:
        uses[i["def"]] = uses.get(i["def"], 0) + 1
    tris_def = sum(pr["nt"] for d in scene["defs"] for pr in d["prims"])
    tris_static = sum(pr["nt"] for pr in scene["statics"])
    tris_all = tris_static + sum(pr["nt"] * uses.get(d["id"], 0) for d in scene["defs"] for pr in d["prims"])
    per = {}
    for d in scene["defs"]:
        c = per.setdefault(d["cat"], [0, 0, 0])
        c[0] += 1
        c[1] += uses.get(d["id"], 0)
        c[2] += sum(pr["nt"] for pr in d["prims"])
    log("por categoria (defs, instancias, tris de defs): %s" % sorted(per.items(), key=lambda kv: -kv[1][2])[:12])
    stats = {"instances": len(scene["insts"]), "defs": len(scene["defs"]), "tris_defs": tris_def,
             "tris_static": tris_static, "tris_rendered": tris_all, "materials": len(p2.materials),
             "seconds": round(time.time() - t0, 1), "bytes": offset}
    log("listo: %s" % stats)
    return stats
