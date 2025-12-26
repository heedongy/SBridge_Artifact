import r2pipe
import sys
from collections import defaultdict
import os

class Radare2Analysis:
    def __init__(self, target):
        self.r2 = r2pipe.open(target)
        self.funcs = {}
        self.functions_dict = {}
        self.call_graph = {}
        self.link_functions = [
            "deregister_tm_clones",
            "register_tm_clones",
            "__do_global_dtors_aux",
            "frame_dummy",
            "call_weak_fn",
            "__libc_csu_init",
            "_fini",
            "__gmon_start__",
            "_init",
            "_DT_INIT",
            "_DT_FINI",
            "_FINI_0"
        ]

    def analyze_binary(self):
        self.r2.cmd('aaa')

    def clean_function_name(self, name):
        if name.startswith('dbg.'):
            name = name[4:]
        if name.startswith('sym.'):
            name = name[4:]
        return name

    def fetch_functions(self):
        self.funcs = self.r2.cmdj("aflj") or []
        for func in self.funcs:
            addr = func.get('offset', func.get('addr'))
            if addr is None or 'name' not in func:
                continue  # Skip invalid entries safely
            clean_name = self.clean_function_name(func['name'])
            self.functions_dict[hex(addr)] = clean_name
            self.functions_dict[clean_name] = hex(addr)

    def fetch_call_references(self):
        self.call_graph = {}
        for func in self.funcs:
            func_name = self.clean_function_name(func['name'])
            if (func_name.startswith('imp') or
                func_name == 'entry' or
                func_name in self.link_functions):
                continue

            addr = func.get('offset', func.get('addr'))
            if addr is None:
                continue

            disasm = self.r2.cmdj(f"pdfj @ {addr}")
            calls = set()

            for op in disasm.get('ops', []):
                if op.get('type') == 'call':
                    call_target = op.get('jump', '')
                    if call_target:
                        if isinstance(call_target, int):
                            call_target = self.functions_dict.get(hex(call_target), '')
                        if (call_target and
                            not call_target.startswith('imp') and
                            call_target not in self.link_functions):
                            calls.add(self.clean_function_name(call_target))

            self.call_graph[func_name] = list(calls)

    def print_call_graph(self):
        for func, calls in self.call_graph.items():
            print(f"{func}: {calls}")


def build_reverse_call_graph(call_graph):
    reverse_graph = defaultdict(set)
    for caller, callees in call_graph.items():
        for callee in callees:
            reverse_graph[callee].add(caller)
    return reverse_graph

def get_existing_ancestors(func, reverse_graph, binary_B_functions, visited=None):
    if visited is None:
        visited = set()
    if func in binary_B_functions:
        return {func}
    if func in visited:
        return set()
    visited.add(func)
    if func not in reverse_graph:
        return set()
    result = set()
    for caller in reverse_graph[func]:
        result |= get_existing_ancestors(caller, reverse_graph, binary_B_functions, visited)
    return result

def inlinefunction_mapping(r2_A, r2_B):
    binary_B_functions = set(r2_B.call_graph.keys())
    A_reverse = build_reverse_call_graph(r2_A.call_graph)

    mapping = {}
    for func in r2_A.call_graph.keys():
        if func in binary_B_functions:
            mapping[func] = {func}
        else:
            mapped = get_existing_ancestors(func, A_reverse, binary_B_functions)
            mapping[func] = mapped
    return mapping


def analyze_binary(binary_O0, binary_O2, target_dir):
    if os.path.exists(os.path.join(target_dir, 'target', binary_O0 + '_strip')):
        strip_binary_O0 = binary_O0 + '_strip'
    else:
        strip_binary_O0 = binary_O0 + '_stripped'

    if os.path.exists(os.path.join(target_dir, 'target', binary_O2 + '_strip')):
        strip_binary_O2 = binary_O2 + '_strip'
    else:
        strip_binary_O2 = binary_O2 + '_stripped'

    binary_O0_info_path = os.path.join(target_dir, 'processed_target','info', binary_O0, 'ground_truth.info')
    binary_O0_strip_info_path = os.path.join(target_dir, 'processed_target','info', strip_binary_O0, 'ground_truth.info')
    binary_O2_info_path = os.path.join(target_dir, 'processed_target','info', binary_O2, 'ground_truth.info')
    binary_O2_strip_info_path = os.path.join(target_dir, 'processed_target','info', strip_binary_O2, 'ground_truth.info')

    if (os.path.exists(binary_O0_info_path) and
        os.path.exists(binary_O0_strip_info_path) and
        os.path.exists(binary_O2_info_path) and
        os.path.exists(binary_O2_strip_info_path)):
        print(f"Ground truth files already exist for {binary_O0} and {binary_O2}. Skipping...")
        return

    r2_O0 = Radare2Analysis(os.path.join(target_dir, 'target', binary_O0))
    r2_O0.analyze_binary()
    r2_O0.fetch_functions()
    r2_O0.fetch_call_references()

    r2_O2 = Radare2Analysis(os.path.join(target_dir, 'target', binary_O2))
    r2_O2.analyze_binary()
    r2_O2.fetch_functions()
    r2_O2.fetch_call_references()

    os.makedirs(os.path.dirname(binary_O0_info_path), exist_ok=True)
    os.makedirs(os.path.dirname(binary_O0_strip_info_path), exist_ok=True)
    os.makedirs(os.path.dirname(binary_O2_info_path), exist_ok=True)
    os.makedirs(os.path.dirname(binary_O2_strip_info_path), exist_ok=True)

    with open(binary_O0_info_path, 'w') as f:
        for a_func in r2_O0.call_graph.keys():
            a_func_name = a_func.split('.')[0] if '.' in a_func else a_func
            offset = r2_O0.functions_dict.get(a_func, "unknown offset")
            f.write(f"{a_func_name}:[{a_func_name}]:[{offset.replace('0x', '')}]\n")

    with open(binary_O0_strip_info_path, 'w') as f:
        for a_func in r2_O0.call_graph.keys():
            a_func_name = a_func.split('.')[0] if '.' in a_func else a_func
            offset = r2_O0.functions_dict.get(a_func, "unknown offset")
            f.write(f"{a_func_name}:[{a_func_name}]:[{offset.replace('0x', '')}]\n")

    with open(binary_O2_info_path, 'w') as f:
        mapping = inlinefunction_mapping(r2_O0, r2_O2)
        for a_func, b_funcs in mapping.items():
            a_func_name = a_func.split('.')[0] if '.' in a_func else a_func
            if b_funcs:
                b_func_names = [func.split('.')[0] if '.' in func else func for func in b_funcs]
                offsets = [r2_O2.functions_dict.get(func, "unknown offset").replace('0x', '') for func in b_funcs]
                f.write(f"{a_func_name}:[{', '.join(b_func_names)}]:[{', '.join(offsets)}]\n")
            else:
                f.write(f"{a_func_name}:[]:[]]\n")

    with open(binary_O2_strip_info_path, 'w') as f:
        mapping = inlinefunction_mapping(r2_O0, r2_O2)
        for a_func, b_funcs in mapping.items():
            a_func_name = a_func.split('.')[0] if '.' in a_func else a_func
            if b_funcs:
                b_func_names = [func.split('.')[0] if '.' in func else func for func in b_funcs]
                offsets = [r2_O2.functions_dict.get(func, "unknown offset").replace('0x', '').replace(' ', '') for func in b_funcs]
                f.write(f"{a_func_name}:[{', '.join(b_func_names)}]:[{', '.join(offsets)}]\n")
            else:
                f.write(f"{a_func_name}:[]:[]]\n")


def main():
    if len(sys.argv) != 2:
        print("Usage: python gt_gen.py <target_project>")
        sys.exit(1)

    target_project = sys.argv[1]
    target_dir = os.path.join(target_project, 'target')
    target_lists = []

    for file in os.listdir(target_dir):
        if 'O0' in file and '_strip' not in file:
            o0_name = file
            o2_name = file.replace('O0', 'O2')
            target_lists.append([o0_name, o2_name])

    print(target_lists)

    for o0_name, o2_name in target_lists:
        analyze_binary(o0_name, o2_name, target_project)

if __name__ == "__main__":
    main()
