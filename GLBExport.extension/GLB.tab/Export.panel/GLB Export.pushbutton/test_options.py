# -*- coding: utf-8 -*-
"""Self-check de options.py. Correr con CPython:  python test_options.py"""
import os
import tempfile

import options as o

CATS = [(u"Puertas", "OST_Doors", 2190), (u"Muros", "OST_Walls", 12035), (u"Ventanas", "OST_Windows", 1050)]


def test_preset_aplica_por_ost_y_ordena():
    rows = o.make_rows(u"Quest (ligero)", CATS)
    assert [r["ost"] for r in rows] == ["OST_Walls", "OST_Doors", "OST_Windows"]
    doors = [r for r in rows if r["ost"] == "OST_Doors"][0]
    assert doors["ratio"] == 3 and doors["lock"] is False and doors["include"] is True
    assert [r for r in rows if r["ost"] == "OST_Walls"][0]["ratio"] == 50
    assert [r for r in o.make_rows(u"Equilibrado", CATS) if r["ost"] == "OST_Walls"][0]["ratio"] == 100


def test_reglas_usan_nombre_visible_y_omiten_100():
    rows = o.make_rows(u"Quest (ligero)", CATS)
    rules = o.build_rules(rows, o.DEFAULT_GENERAL)
    assert set(rules["byCategory"]) == {u"Puertas", u"Ventanas", u"Muros"}
    assert rules["byCategory"][u"Puertas"] == {"ratio": 0.03, "errorM": 0.1, "lockBorder": False}
    assert rules["byCategory"][u"Ventanas"]["lockBorder"] is True
    assert rules["minInstances"] == 20 and rules["mergeStatics"] is True and rules["recenter"] is True and rules["minTris"] == 300


def test_sin_optimizar_no_simplifica():
    rows = o.make_rows(u"Sin optimizar", CATS)
    assert o.build_rules(rows, o.DEFAULT_GENERAL)["byCategory"] == {}


def test_categoria_excluida_no_se_simplifica_y_se_salta():
    rows = o.make_rows(u"Quest (ligero)", CATS)
    for r in rows:
        if r["ost"] == "OST_Doors":
            r["include"] = False
    assert o.excluded_names(rows) == {u"Puertas"}
    assert u"Puertas" not in o.build_rules(rows, o.DEFAULT_GENERAL)["byCategory"]


def test_pisos_escaleras_puertas_y_ventanas_no_se_fusionan():
    cats = CATS + [(u"Suelos", "OST_Floors", 1743), (u"Topografia", "OST_Toposolid", 43), (u"Escaleras", "OST_Stairs", 38), (u"Rampas", "OST_Ramps", 2)]
    rules = o.build_rules(o.make_rows(u"Quest (ligero)", cats), o.DEFAULT_GENERAL)
    # puertas y ventanas (CATS trae Puertas y Ventanas) tambien quedan con nombre propio
    assert sorted(rules["keepSeparate"]) == sorted([u"Suelos", u"Topografia", u"Escaleras", u"Rampas", u"Puertas", u"Ventanas"])


def test_sin_instancias_sube_el_minimo():
    g = dict(o.DEFAULT_GENERAL, use_instances=False)
    assert o.build_rules([], g)["minInstances"] == 10 ** 9


def test_caras_color_y_celdas_por_defecto():
    rules = o.build_rules([], o.DEFAULT_GENERAL)
    assert rules["doubleSidedAll"] is False and rules["vertexColors"] is True and rules["cellM"] == 0
    assert rules["doubleSided"] == []


def test_una_cara_se_puede_apagar():
    assert o.build_rules([], dict(o.DEFAULT_GENERAL, one_sided=False))["doubleSidedAll"] is True
    assert o.build_rules([], dict(o.DEFAULT_GENERAL, vertex_colors=False))["vertexColors"] is False


def test_superficies_abiertas_van_a_doble_cara():
    cats = CATS + [(u"Topografia", "OST_Topography", 3), (u"Vegetacion", "OST_Planting", 40)]
    rules = o.build_rules(o.make_rows(u"Quest (ligero)", cats), o.DEFAULT_GENERAL)
    assert sorted(rules["doubleSided"]) == [u"Topografia", u"Vegetacion"]


def test_celdas_se_acotan():
    cell = lambda v: o.build_rules([], dict(o.DEFAULT_GENERAL, cell_m=v))["cellM"]
    assert cell(20) == 20 and cell("x") == 0 and cell(-5) == 0 and cell(9999) == 500
    assert cell(0.0001) == 0 and cell(1.5) == 0 and cell(2) == 2      # celdas de menos de 2 m generarian miles de mallas


def test_perfil_viejo_toma_los_valores_nuevos_por_defecto():
    p = os.path.join(tempfile.mkdtemp(), "last.json")
    open(p, "w").write('{"preset": "Equilibrado", "general": {"min_tris": 500}, "rows": {}}')
    _, general, _ = o.load_profile(p)
    assert general["min_tris"] == 500 and general["one_sided"] is True and general["vertex_colors"] is True and general["cell_m"] == 0


def test_valores_invalidos_se_acotan():
    rows = o.make_rows(u"Sin optimizar", CATS)
    rows[0].update(ratio="abc")          # no numerico: queda en 100, sin regla
    rows[1].update(ratio=0, error_cm=-5)  # fuera de rango: se acota a 1 % y 0,1 cm
    rules = o.build_rules(rows, dict(o.DEFAULT_GENERAL, min_tris="x"))
    assert rows[0]["name"] not in rules["byCategory"]
    r = rules["byCategory"][rows[1]["name"]]
    assert r["ratio"] == 0.01 and r["errorM"] == 0.001
    assert rules["minTris"] == 300


def test_perfil_ida_y_vuelta_y_tolerancia():
    p = os.path.join(tempfile.mkdtemp(), "sub", "last.json")
    assert o.load_profile(p) == (o.DEFAULT_PRESET, o.DEFAULT_GENERAL, {})      # no existe
    rows = o.make_rows(u"Equilibrado", CATS)
    rows[1]["ratio"] = 33
    o.save_profile(u"Equilibrado", dict(o.DEFAULT_GENERAL, min_tris=500), rows, p)
    preset, general, saved = o.load_profile(p)
    assert preset == u"Equilibrado" and general["min_tris"] == 500
    again = o.make_rows(preset, CATS, saved)
    assert [r for r in again if r["ost"] == rows[1]["ost"]][0]["ratio"] == 33
    open(p, "w").write("{no es json")
    assert o.load_profile(p)[0] == o.DEFAULT_PRESET                            # danado


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_"):
            fn()
            print("ok  " + name)
