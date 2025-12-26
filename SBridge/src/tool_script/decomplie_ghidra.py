from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor
import os
import json
import codecs
import sys


script_dir = os.path.dirname(os.path.abspath(__file__))
config_path = os.path.join(script_dir, 'config.json')
with open(config_path, 'r') as config_file:
    config = json.load(config_file)

def normalize_path(path):
    if path.startswith('~'):
        return os.path.expanduser(path)
    return os.path.abspath(path)

INPUT_PATH = normalize_path(config['input_path'])
DETAIL_PATH = os.path.join(INPUT_PATH, "processed_target/decompileCode")

def get_output_filenames():
    program_name = currentProgram.getDomainFile().getName()
    base_name = program_name
    return os.path.join(DETAIL_PATH, base_name)

def decompile_functions():
    monitor = ConsoleTaskMonitor()
    decompInterface = DecompInterface()

    if not decompInterface.openProgram(currentProgram):
        print("Failed to initialize DecompInterface.")
        return

    function_manager = currentProgram.getFunctionManager()
    functions = list(function_manager.getFunctions(True))

    details_dir = get_output_filenames()
    if not os.path.exists(details_dir):
        os.makedirs(details_dir)

    for function in functions:
        func_name = function.getName()

        if function.isThunk():
            continue
        if func_name == 'entry' or func_name == '_start' or func_name.startswith('_'):
            continue

        result = decompInterface.decompileFunction(function, 30, monitor)
        if not result.decompileCompleted():
            print("Decompilation error for function {}: {}".format(func_name, result.errorMessage))
            continue

        decompiled_func = result.getDecompiledFunction().getC()

        lines = decompiled_func.split('\n')
        start_idx = 0
        for i, line in enumerate(lines):
            line_strip = line.strip()
            if (not line_strip.startswith('/*') and
                not line_strip.startswith('//') and
                not line_strip == ''):
                start_idx = i
                break

        decompiled_func = '\n'.join(lines[start_idx:])

        func_file_path = os.path.join(details_dir, "{}.c".format(func_name))
        with codecs.open(func_file_path, 'w', 'utf-8') as f:
            f.write(decompiled_func)

if __name__ == "__main__":
    decompile_functions()
