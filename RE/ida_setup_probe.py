import ida_auto
import ida_funcs
import ida_hexrays
import ida_kernwin
import ida_name

ida_auto.auto_wait()

targets = (
    "SeedManager$$GeneratePlacement",
    "MainMenu$$Start",
    "MainMenu$$Update",
    "Paused$$Start",
    "Paused$$RestartP",
    "Menu_Seed$$Start",
    "Menu_Seed$$Update",
    "Menu_Seed$$ExitTypingModeAndSave",
    "ObjectsManager$$Start",
    "ObjectsManager$$Update",
    "ItemPresetSetup$$Start",
)

for name in targets:
    ea = ida_name.get_name_ea(ida_name.BADADDR, name)
    print("\n===== %s @ %x =====" % (name, ea))
    if ea == ida_name.BADADDR:
        continue
    try:
        cfunc = ida_hexrays.decompile(ea)
        print(str(cfunc) if cfunc else "NO DECOMPILATION")
    except Exception as exc:
        print("DECOMPILE ERROR: %r" % (exc,))

ida_kernwin.qexit(0)
