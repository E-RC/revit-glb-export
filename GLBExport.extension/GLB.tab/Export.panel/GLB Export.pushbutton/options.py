# -*- coding: utf-8 -*-
"""Opciones de GLB Export: presets, reglas para build.cjs y perfil guardado.

Logica pura (sin Revit ni WPF): compatible con IronPython 2.7 y verificable con CPython
(python test_options.py). Las categorias se identifican por su nombre BuiltInCategory
(OST_Doors) para que los presets valgan con Revit en cualquier idioma; el nombre visible
(el que usa el exportador en los nodos del GLB) viene de Revit.
"""
import io
import json
import os

# ratio = % de triangulos que se conserva; error_cm = error maximo permitido en cm
PRESETS = {
    u"Quest (ligero)": {
        "OST_Doors": dict(ratio=3, error_cm=10.0, lock=False),
        "OST_Entourage": dict(ratio=4, error_cm=25.0, lock=False),
        "OST_Windows": dict(ratio=30, error_cm=2.0, lock=True),
        "OST_VerticalCirculation": dict(ratio=20, error_cm=5.0, lock=False),
        "OST_Floors": dict(ratio=30, error_cm=0.5, lock=False),
        "OST_Walls": dict(ratio=50, error_cm=0.5, lock=False),
        "OST_Toposolid": dict(ratio=30, error_cm=3.0, lock=False),
    },
    u"Equilibrado": {
        "OST_Doors": dict(ratio=10, error_cm=5.0, lock=False),
        "OST_Entourage": dict(ratio=15, error_cm=10.0, lock=False),
        "OST_Windows": dict(ratio=60, error_cm=1.0, lock=True),
        "OST_VerticalCirculation": dict(ratio=50, error_cm=3.0, lock=False),
    },
    u"Sin optimizar": {},
}
DEFAULT_PRESET = u"Quest (ligero)"
MIN_INSTANCES = 20           # bajo este numero de copias se funde con la geometria fija (menos draw calls)
SEPARATE_OST = ("OST_Floors", "OST_Toposolid", "OST_Stairs", "OST_Ramps", "OST_Doors", "OST_Windows")   # piezas con nombre propio: pisos, escaleras, rampas y terreno (se camina sobre ellos) y puertas y ventanas (se atraviesan)
OPEN_SURFACE_OST = ("OST_Topography", "OST_Planting", "OST_Entourage", "OST_Roads", "OST_Site")   # superficies abiertas o planas: se ven por las dos caras
# one_sided: descarta la cara trasera (salvo categorias abiertas o con datos inconsistentes); vertex_colors: un solo
# material opaco con el color en los vertices (menos draw calls); cell_m: parte la geometria fija en celdas de ese
# tamano en metros (0 = no; mas mallas para recortar fuera de camara, a probar en el casco antes de usarlo)
DEFAULT_GENERAL = dict(min_tris=300, use_instances=True, recenter=True, include_links=True,
                       one_sided=True, vertex_colors=True, cell_m=0)
NEUTRAL = dict(ratio=100, error_cm=2.0, lock=False)


def preset_names():
    return [u"Quest (ligero)", u"Equilibrado", u"Sin optimizar"]


def make_rows(preset, categories, saved=None):
    """categories: [(nombre_visible, ost, cantidad)]. Devuelve filas editables (dicts).

    `saved` (perfil previo, por ost) pisa al preset solo para categorias que ya tenian ajuste.
    """
    rules = PRESETS.get(preset, {})
    saved = saved or {}
    rows = []
    for name, ost, count in categories:
        base = dict(NEUTRAL)
        base.update(rules.get(ost, {}))
        base.update(saved.get(ost, {}))
        rows.append(dict(name=name, ost=ost, count=count, include=base.pop("include", True), **base))
    return sorted(rows, key=lambda r: -r["count"])


def clamp(value, lo, hi, default):
    try:
        v = float(value)
    except (TypeError, ValueError):
        return default
    return max(lo, min(hi, v))


def min_cell(value):
    """Tamano de celda en metros: 0 (no partir) o entre 2 y 500; lo menor a 2 m se descarta."""
    c = clamp(value, 0, 500, 0)
    return c if c >= 2 else 0


def build_rules(rows, general):
    """Reglas para build.cjs (--rules). Solo las categorias que se simplifican."""
    by_cat = {}
    for r in rows:
        ratio = clamp(r["ratio"], 1, 100, 100)
        if ratio >= 100 or not r["include"]:
            continue
        by_cat[r["name"]] = {"ratio": ratio / 100.0,
                             "errorM": clamp(r["error_cm"], 0.1, 100, 2.0) / 100.0,
                             "lockBorder": bool(r["lock"])}
    return {"minTris": int(clamp(general.get("min_tris"), 0, 100000, 300)),
            "minInstances": MIN_INSTANCES if general.get("use_instances", True) else 10 ** 9,
            "mergeStatics": True,
            "keepSeparate": [r["name"] for r in rows if r.get("ost") in SEPARATE_OST],
            "recenter": bool(general.get("recenter", True)),
            "doubleSidedAll": not general.get("one_sided", True),
            "doubleSided": [r["name"] for r in rows if r.get("ost") in OPEN_SURFACE_OST],
            "vertexColors": bool(general.get("vertex_colors", True)),
            "cellM": min_cell(general.get("cell_m")),
            "byCategory": by_cat}


def excluded_names(rows):
    return set(r["name"] for r in rows if not r["include"])


def profile_path():
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, "Archiplan", "GlbExport", "last.json")


def save_profile(preset, general, rows, path=None):
    path = path or profile_path()
    folder = os.path.dirname(path)
    if not os.path.isdir(folder):
        os.makedirs(folder)
    data = {"preset": preset, "general": general,
            "rows": dict((r["ost"], dict(include=bool(r["include"]), ratio=r["ratio"],
                                         error_cm=r["error_cm"], lock=bool(r["lock"]))) for r in rows)}
    with io.open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(data, ensure_ascii=False, indent=1))


def load_profile(path=None):
    """Perfil previo o valores por defecto si no existe o esta danado."""
    path = path or profile_path()
    try:
        with io.open(path, "r", encoding="utf-8") as f:
            data = json.loads(f.read())
        general = dict(DEFAULT_GENERAL)
        general.update(data.get("general", {}))
        preset = data.get("preset")
        return (preset if preset in PRESETS else DEFAULT_PRESET), general, data.get("rows", {})
    except (IOError, OSError, ValueError):
        return DEFAULT_PRESET, dict(DEFAULT_GENERAL), {}
