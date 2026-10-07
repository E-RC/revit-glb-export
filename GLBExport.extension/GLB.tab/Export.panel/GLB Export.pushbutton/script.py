# -*- coding: utf-8 -*-
"""GLB Export: vista 3D activa -> scene.json/data.bin -> GLB (GlbExport/src/build.cjs).

Dialogo con perfil de optimizacion y reglas por categoria. La logica de opciones vive en
options.py (verificable con CPython); la geometria en exporter.py.
"""
import json
import os
import shutil
import subprocess
import tempfile

from pyrevit import revit, DB, forms, script

import exporter
import options

HERE = os.path.dirname(__file__)


def find_arch_tools(start):
    """Sube desde el botón hasta la carpeta ARCH_Tools (la que contiene GlbExport); no depende de en qué extensión viva."""
    d = os.path.abspath(start)
    for _ in range(8):
        if os.path.isdir(os.path.join(d, "GlbExport")):
            return d
        d = os.path.dirname(d)
    raise RuntimeError("No encuentro la carpeta GlbExport sobre %s" % start)


ARCH_TOOLS = find_arch_tools(HERE)
BUILD = os.path.join(ARCH_TOOLS, "GlbExport", "src", "build.cjs")
DEPS = os.path.join(os.environ.get("LOCALAPPDATA", ""), "ArchiGlb", "node_modules")


def find_node():
    dirs = os.environ.get("PATH", "").split(os.pathsep)
    for var in ("ProgramFiles", "ProgramW6432"):
        if os.environ.get(var):
            dirs.append(os.path.join(os.environ[var], "nodejs"))
    if os.environ.get("LOCALAPPDATA"):
        dirs.append(os.path.join(os.environ["LOCALAPPDATA"], "Programs", "nodejs"))
    for p in dirs:
        exe = os.path.join(p.strip('"'), "node.exe")
        if os.path.isfile(exe):
            return exe
    return None


def _eid(e):
    try:
        return e.Value
    except AttributeError:
        return e.IntegerValue


def view_categories(doc, view):
    """[(nombre, ost, cantidad)] de categorias de modelo con elementos en la vista."""
    found = {}
    for el in DB.FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType():
        c = el.Category
        if c is None or c.CategoryType != DB.CategoryType.Model:
            continue
        key = _eid(c.Id)
        if key not in found:
            try:
                ost = str(DB.BuiltInCategory(key))
            except Exception:
                ost = "id:%d" % key
            found[key] = [c.Name, ost, 0]
        found[key][2] += 1
    return [tuple(v) for v in found.values()]


class CatRow(object):
    """Fila del DataGrid. Las propiedades son las que enlaza el XAML."""

    def __init__(self, row):
        self.row = row

    @property
    def Include(self):
        return self.row["include"]

    @Include.setter
    def Include(self, v):
        self.row["include"] = bool(v)

    @property
    def Name(self):
        return self.row["name"]

    @property
    def Count(self):
        return self.row["count"]

    @property
    def Ratio(self):
        return self.row["ratio"]

    @Ratio.setter
    def Ratio(self, v):
        self.row["ratio"] = options.clamp(v, 1, 100, self.row["ratio"])

    @property
    def ErrorCm(self):
        return self.row["error_cm"]

    @ErrorCm.setter
    def ErrorCm(self, v):
        self.row["error_cm"] = options.clamp(v, 0.1, 100, self.row["error_cm"])

    @property
    def Lock(self):
        return self.row["lock"]

    @Lock.setter
    def Lock(self, v):
        self.row["lock"] = bool(v)


class ExportWindow(forms.WPFWindow):
    def __init__(self, xaml_file_name, view, categories):
        forms.WPFWindow.__init__(self, xaml_file_name)
        self.result = None
        self.categories = categories
        self._loading = True
        preset, general, saved = options.load_profile()
        self._saved = saved
        self.view_txt.Text = u"Vista: %s" % view.Name
        self.out_txt.Text = os.path.join(os.path.expanduser("~"), "Documents", u"%s.glb" % view.Name)
        for name in options.preset_names():
            self.preset_cb.Items.Add(name)
        self.preset_cb.SelectedItem = preset
        self.inst_chk.IsChecked = general["use_instances"]
        self.center_chk.IsChecked = general["recenter"]
        self.links_chk.IsChecked = general["include_links"]
        self.mintris_txt.Text = str(int(general["min_tris"]))
        self.onesided_chk.IsChecked = general["one_sided"]
        self.vcolor_chk.IsChecked = general["vertex_colors"]
        self.cell_txt.Text = str(int(general["cell_m"]))
        self._fill(preset, saved)
        self._loading = False

    def _fill(self, preset, saved=None):
        self.rows = options.make_rows(preset, self.categories, saved)
        self.cat_grid.ItemsSource = [CatRow(r) for r in self.rows]

    def preset_changed(self, sender, args):
        if self._loading or self.preset_cb.SelectedItem is None:
            return
        self._fill(self.preset_cb.SelectedItem)

    def browse_click(self, sender, args):
        path = forms.save_file(file_ext="glb", default_name=os.path.basename(self.out_txt.Text))
        if path:
            self.out_txt.Text = path

    def cancel_click(self, sender, args):
        self.Close()

    def export_click(self, sender, args):
        self.cat_grid.CommitEdit()
        out = self.out_txt.Text.strip()
        if not out or not os.path.isdir(os.path.dirname(out) or "."):
            forms.alert(u"Elige una carpeta de destino que exista.")
            return
        if not out.lower().endswith(".glb"):
            out += ".glb"
        general = dict(use_instances=bool(self.inst_chk.IsChecked), recenter=bool(self.center_chk.IsChecked),
                       include_links=bool(self.links_chk.IsChecked),
                       one_sided=bool(self.onesided_chk.IsChecked), vertex_colors=bool(self.vcolor_chk.IsChecked),
                       cell_m=options.clamp(self.cell_txt.Text, 0, 500, 0),
                       min_tris=options.clamp(self.mintris_txt.Text, 0, 100000, 300))
        preset = self.preset_cb.SelectedItem
        options.save_profile(preset, general, self.rows)
        self.result = dict(out=out, general=general, rules=options.build_rules(self.rows, general),
                           exclude=options.excluded_names(self.rows))
        self.Close()


def run_export(view, cfg, node, log):
    tmp = tempfile.mkdtemp(prefix="glbexport_")
    try:
        stats = exporter.export_view(revit.doc, view, tmp, log, exclude=cfg["exclude"],
                                     include_links=cfg["general"]["include_links"])
        rules = os.path.join(tmp, "rules.json")
        with open(rules, "w") as f:
            json.dump(cfg["rules"], f)
        log("convirtiendo a GLB...")
        p = subprocess.Popen([node, BUILD, tmp, cfg["out"], "--rules", rules], stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, creationflags=0x08000000)
        so, se = p.communicate()
        if p.returncode != 0:
            raise RuntimeError("Fallo la conversion a GLB:\n" + (se or so)[-800:])
        return stats, so.strip()
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    view = revit.active_view
    if not isinstance(view, DB.View3D):
        forms.alert(u"Abre una vista 3D y vuelve a ejecutar.", exitscript=True)
    node = find_node()
    if not node or not os.path.isdir(DEPS):
        forms.alert(u"Falta Node.js o sus dependencias.\nEjecuta install.ps1 (o el instalador .exe) y reinicia Revit.", exitscript=True)
    cats = view_categories(revit.doc, view)
    if not cats:
        forms.alert(u"La vista no tiene elementos de modelo visibles.", exitscript=True)

    win = ExportWindow("ui.xaml", view, cats)
    win.ShowDialog()
    cfg = win.result
    if not cfg:
        return

    output = script.get_output()
    output.print_md(u"### GLB Export\nVista **%s**. Esto toma unos minutos; Revit queda ocupado." % view.Name)
    stats, summary = run_export(view, cfg, node, lambda s: output.print_md(u"- %s" % s))
    output.print_md(u"**Listo.** `%s`\n\n`%s`" % (cfg["out"], summary))
    forms.alert(u"Listo.\n\n%s\n\nInstancias: %d\nTriangulos sin optimizar: %d" % (
        cfg["out"], stats["instances"], stats["tris_rendered"]))


main()
