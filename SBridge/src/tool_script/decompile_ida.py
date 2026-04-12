import idaapi
import ida_hexrays
import idautils
import os
import codecs
import sys
import json
import re

script_dir = os.path.dirname(os.path.abspath(__file__))
config_path = os.path.join(script_dir, 'config.json')
with open(config_path, 'r') as config_file:
    config = json.load(config_file)

def normalize_path(path):
    if path.startswith('~'):
        return os.path.expanduser(path)
    return os.path.abspath(path)

INPUT_PATH = normalize_path(config['input_path'])
DETAIL_PATH = INPUT_PATH + "/processed_target/decompileCode_ida"

def get_output_filenames():
    binary_name = idaapi.get_root_filename()
    return os.path.join(DETAIL_PATH, binary_name)

def normalize_code(decompiled_code):
    lines = decompiled_code.splitlines()
    if lines:
        lines[0] = re.sub(r'^(\w+)\s+.*?(?=\w+\s*\()', r'\1 ', lines[0].strip())
    return "\n".join(lines)

def decompile_functions():
    if not ida_hexrays.init_hexrays_plugin():
        print("Failed to initialize Hex-rays decompiler")
        return False

    details_dir = get_output_filenames()
    os.makedirs(details_dir, exist_ok=True)

    for func_ea in idautils.Functions():
        func_name = idc.get_func_name(func_ea)

        # Skip specific functions
        if func_name.startswith('_') or func_name.startswith('.') or func_name == 'entry':
            continue

        try:
            cfunc = ida_hexrays.decompile(func_ea)
            if cfunc:
                decompiled_code = str(cfunc)
                normalized_code = normalize_code(decompiled_code)
                output_path = os.path.join(details_dir, f"{func_name}.c")

                with codecs.open(output_path, 'w', 'utf-8') as f:
                    f.write(normalized_code)
                print(f"Successfully decompiled function: {func_name}")
            else:
                print(f"Failed to decompile function: {func_name}")
        except Exception as e:
            print(f"Error while decompiling {func_name}: {str(e)}")

    return True

if __name__ == "__main__":
    idaapi.auto_wait()  # Wait for the database to be fully loaded
    idaapi.set_database_flag(idaapi.DBFL_KILL)

    success = decompile_functions()
    if success:
        print("Decompilation completed successfully.")
    else:
        print("Decompilation failed.")
    idaapi.qexit(0)