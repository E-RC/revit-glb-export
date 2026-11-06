# -*- coding: UTF-8 -*-
"""GLB Export como boton de pyRevit: ejecuta el comando del add-in compilado (DLL en bin/).

install.ps1 -PyRevitDir deja una DLL por version de Revit (GlbExport_<anio>.dll) en la carpeta bin de la
extension; este boton carga la que corresponde y ejecuta GlbExport.ExportCommand, sin registrar el .addin
ni crear la pestana GLB propia.
"""
import os

import System
from Autodesk.Revit.DB import ElementSet
from pyrevit import EXEC_PARAMS, HOST_APP, forms

HERE = os.path.dirname(__file__)
# bundle -> panel -> tab -> extension
EXTENSION_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
DLL = os.path.join(EXTENSION_ROOT, "bin", "GlbExport_%s.dll" % HOST_APP.version)
COMMAND_CLASS = "GlbExport.ExportCommand"


def run():
    if not os.path.isfile(DLL):
        forms.alert(u"No hay una versión de GLB Export para Revit %s.\n\nFalta: %s" % (HOST_APP.version, DLL),
                    title=u"GLB Export", warn_icon=True)
        return
    assembly = System.Reflection.Assembly.LoadFrom(DLL)
    command_type = assembly.GetType(COMMAND_CLASS)
    if command_type is None:
        forms.alert(u"La DLL no contiene %s." % COMMAND_CLASS, title=u"GLB Export", warn_icon=True)
        return
    command = System.Activator.CreateInstance(command_type)
    command.Execute(EXEC_PARAMS.command_data, "", ElementSet())


run()
