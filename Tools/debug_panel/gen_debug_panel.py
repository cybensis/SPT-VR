# Builds the UnityExplorer debug panel (one HTML page) from the [Tunable] markers in the source and DevTools/FikaSyncKnobs.cs.
#   python -I Tools/debug_panel/gen_debug_panel.py <repo> <out.html>
# sections.json: the panel's section order, titles and blurbs, and the options of enum knobs. template.html: the page.
# Each row's UnityExplorer line is worked out from the source: the class path (outer+nested), and whether the member or any class
# around it is non-public (then the line goes through HarmonyLib.AccessTools, as the console can't name it).
import glob
import json
import os
import re
import sys

KIND_TAG = {'Switch': 'switch', 'Tune': 'tune', 'Log': 'log', 'View': 'log', 'Test': 'test', 'Action': 'action'}
SKIPPED_DIRS = ('/bin/', '/obj/', '/.claude/', '/FikaSync/', '/DevTools/')


def source_files(repo):
    for path in sorted(glob.glob(os.path.join(repo, '**', '*.cs'), recursive=True)):
        norm = path.replace('\\', '/')
        if not any(part in norm for part in SKIPPED_DIRS):
            yield norm


def blank_strings_and_comments(text):
    # same length, with comments and string / char literals blanked (interpolation holes kept), so braces can be counted
    out = list(text)
    n = len(text)

    def blank(a, b):
        for k in range(a, b):
            if out[k] != '\n':
                out[k] = ' '

    def starts_string(i):
        return text[i] == '"' or (text[i] in '@$' and i + 1 < n and text[i + 1] in '"@$')

    def skip_char(i):
        j = i + 1
        while j < n and text[j] != "'":
            j += 2 if text[j] == '\\' else 1
        blank(i + 1, j)
        return j + 1

    def skip_string(i):
        # text[i] starts a string literal (", @", $", $@", @$"): blanks its text, returns the index after it
        verbatim = interpolated = False
        while text[i] in '@$':
            verbatim |= text[i] == '@'
            interpolated |= text[i] == '$'
            i += 1
        i += 1
        start = i
        while i < n:
            c = text[i]
            if not verbatim and c == '\\':
                i += 2
                continue
            if c == '"':
                if verbatim and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                blank(start, i)
                return i + 1
            if interpolated and c == '{':
                if i + 1 < n and text[i + 1] == '{':
                    i += 2
                    continue
                blank(start, i)
                i = skip_hole(i + 1)
                start = i
                continue
            i += 1
        blank(start, n)
        return n

    def skip_hole(i):
        # code inside an interpolation hole up to its closing brace
        depth = 0
        while i < n:
            c = text[i]
            if starts_string(i):
                i = skip_string(i)
                continue
            if c == "'":
                i = skip_char(i)
                continue
            if c == '{':
                depth += 1
            elif c == '}':
                if depth == 0:
                    return i + 1
                depth -= 1
            i += 1
        return n

    i = 0
    while i < n:
        if text.startswith('//', i):
            j = text.find('\n', i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif text.startswith('/*', i):
            j = text.find('*/', i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
        elif starts_string(i):
            i = skip_string(i)
        elif text[i] == "'":
            i = skip_char(i)
        else:
            i += 1
    return ''.join(out)


def type_scopes(text):
    # [(start, end, namespace, [(name, declared public)])] for every class / struct body in the file
    code = blank_strings_and_comments(text)
    scopes, stack = [], []
    pending = None
    pattern = re.compile(r'\bnamespace\s+([\w\.]+)|\b((?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly)\s+)*)'
                         r'(class|struct|interface|enum)\s+(\w+)|[{}]')
    for m in pattern.finditer(code):
        token = m.group(0)
        if m.group(1):
            pending = ('namespace', m.group(1), True)
        elif m.group(3):
            pending = ('type', m.group(4), 'public' in (m.group(2) or ''))
        elif token == '{':
            stack.append((pending or ('block', None, True), m.start()))
            pending = None
        elif stack:
            (kind, name, public), start = stack.pop()
            if kind == 'type':
                chain = [(n, p) for (k, n, p), _ in stack if k == 'type'] + [(name, public)]
                namespace = '.'.join(n for (k, n, p), _ in stack if k == 'namespace')
                scopes.append((start, m.start(), namespace, chain))
    return scopes


def split_args(text):
    # top-level comma split of an argument list (strings kept whole)
    args, depth, current, i = [], 0, '', 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = i + 1
            while text[j] != '"':
                j += 2 if text[j] == '\\' else 1
            current += text[i:j + 1]
            i = j + 1
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        if c == ',' and depth == 0:
            args.append(current.strip())
            current = ''
        else:
            current += c
        i += 1
    if current.strip():
        args.append(current.strip())
    return args


def string_value(expression):
    # "a" + "b" ... -> ab (C# escapes undone)
    parts = re.findall(r'"((?:[^"\\]|\\.)*)"', expression)
    return ''.join(json.loads('"' + part + '"') for part in parts)


def literal_value(text):
    text = text.strip()
    if text in ('true', 'false'):
        return text == 'true'
    if text.startswith('"'):
        return string_value(text)
    m = re.fullmatch(r'(-?[\d\.]+(?:[eE]-?\d+)?)[fFdDmM]?', text)
    if m:
        value = float(m.group(1))
        return int(value) if re.fullmatch(r'-?\d+', m.group(1)) else value
    if text in ('Vector3.zero', 'UnityEngine.Vector3.zero'):
        return [0, 0, 0]
    if text in ('Vector3.one', 'UnityEngine.Vector3.one'):
        return [1, 1, 1]
    m = re.fullmatch(r'new\s+(?:UnityEngine\.)?Vector3\((.*)\)', text)
    if m:
        return [literal_value(a) for a in split_args(m.group(1))]
    m = re.fullmatch(r'[\w\.]+\.(\w+)', text)
    if m:
        return m.group(1)
    return text


def public_types(repo):
    # (namespace, Outer.Inner) of every type with a part declared public: a partial part may leave its access to another part
    found = set()
    for path in source_files(repo):
        for start, end, namespace, chain in type_scopes(open(path, encoding='utf-8-sig').read()):
            if chain[-1][1]:
                found.add((namespace, '.'.join(name for name, _ in chain)))
    return found


def attribute_end(text, i):
    # i is just past "[Tunable(": the index after its closing parenthesis
    depth = 1
    while depth:
        c = text[i]
        if c == '"':
            i += 1
            while text[i] != '"':
                i += 2 if text[i] == '\\' else 1
        elif c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
        i += 1
    return i


def markers(repo):
    rows = []
    public = public_types(repo)
    declaration = re.compile(r'\s*((?:(?:public|internal|private|protected|static|readonly)\s+)+)([\w<>\[\],\.\?]+)\s+(\w+)\s*(=\s*([^;]+);|;|\(|=>)')
    for path in source_files(repo):
        text = open(path, encoding='utf-8-sig').read()
        if '[Tunable(' not in text:
            continue
        scopes = type_scopes(text)
        for m in re.finditer(r'\[Tunable\(', text):
            end = attribute_end(text, m.end())
            args = split_args(text[m.end():end - 1])
            named = {}
            for arg in args[3:]:
                key, value = arg.split('=', 1)
                named[key.strip()] = value.strip()
            section, kind, description = string_value(args[0]), args[1].split('.')[-1], string_value(args[2])
            decl = declaration.match(text, end + 1)
            if not decl:
                print('WARNING: no member after a marker in', path)
                continue
            if kind == 'Status':
                continue
            modifiers, member_type, name = decl.group(1), decl.group(2), decl.group(3)
            scope = max((s for s in scopes if s[0] <= m.start() <= s[1]), key=lambda s: s[0], default=None)
            if scope is None:
                print('WARNING: marker outside a class in', path)
                continue
            namespace, chain = scope[2], scope[3]
            names = [n for n, _ in chain]
            hidden = 'public' not in modifiers or not all((namespace, '.'.join(names[:k + 1])) in public for k in range(len(names)))
            cls = namespace + '.' + ('+'.join(names) if hidden else '.'.join(names))
            row = {'cls': cls, 'f': name, 'tag': KIND_TAG[kind], 'what': description, '_section': section}
            if 'Unit' in named:
                row['unit'] = string_value(named['Unit'])
            if kind == 'Action' or decl.group(4) == '(':
                row['t'] = 'action'
                row['tag'] = 'action'
            else:
                initializer = decl.group(5)
                row['t'] = {'bool': 'bool', 'float': 'float', 'int': 'int', 'Vector3': 'vec3'}.get(member_type, 'enum')
                if row['t'] == 'enum':
                    row['enumType'] = member_type
                row['d'] = literal_value(initializer) if initializer else {'bool': False, 'float': 0, 'int': 0, 'vec3': [0, 0, 0]}.get(row['t'])
            if 'Suggested' in named:
                row['set'] = literal_value(named['Suggested'])
            if hidden:
                row['internal'] = True
            rows.append(row)
    return rows


def fikasync_rows(repo):
    text = open(os.path.join(repo, 'DevTools', 'FikaSyncKnobs.cs'), encoding='utf-8-sig').read()
    rows = []
    knob = re.compile(r'new Knob \{ type = "([^"]+)", field = "(\w+)", section = "([^"]+)", kind = TunableKind\.(\w+), '
                      r'unit = (null|"[^"]*"),\s*description = ("(?:[^"\\]|\\.)*") \}')
    for m in knob.finditer(text):
        type_name, field, section, kind, unit, description = m.groups()
        short = type_name.split('.')[-1]
        default, member_type = None, 'float'
        candidates = glob.glob(os.path.join(repo, 'FikaSync', '*.cs')) + glob.glob(os.path.join(repo, 'Source', '**', short + '.cs'), recursive=True)
        for path in candidates:
            source = open(path, encoding='utf-8-sig').read()
            d = re.search(r'static\s+(?:readonly\s+)?(\w+)\s+' + field + r'\s*=\s*([^;]+);', source)
            if d and re.search(r'class\s+' + short + r'\b', source):
                member_type, default = d.group(1), literal_value(d.group(2))
                break
        row = {'cls': type_name + ', SPT-VR-FikaSync', 'f': field, 't': {'bool': 'bool', 'int': 'int'}.get(member_type, 'float'),
               'd': default, 'tag': KIND_TAG[kind], 'internal': True, 'what': json.loads(description), '_section': section}
        if unit != 'null':
            row['unit'] = json.loads(unit)
        rows.append(row)
    return rows


def main():
    repo, out_path = sys.argv[1], sys.argv[2]
    here = os.path.dirname(os.path.abspath(__file__))
    meta = json.load(open(os.path.join(here, 'sections.json'), encoding='utf-8'))
    rows = markers(repo) + fikasync_rows(repo)
    by_title = {}
    for row in rows:
        if row.get('t') == 'enum':
            short = row['enumType'].split('.')[-1]
            full = next((k for k in meta['enums'] if k.split('.')[-1] == short), row['enumType'])
            row['enumType'] = full
            row['options'] = meta['enums'].get(full) or [row['d']]
        by_title.setdefault(row.pop('_section'), []).append(row)
    sections = []
    for section in meta['sections']:
        sections.append({'id': section['id'], 'title': section['title'], 'blurb': section['blurb'], 'rows': by_title.pop(section['title'], [])})
    for title, extra in by_title.items():
        sections.append({'id': re.sub(r'\W+', '-', title.lower()), 'title': title, 'blurb': '', 'rows': extra})
    template = open(os.path.join(here, 'template.html'), encoding='utf-8').read()
    page = template.replace('/*SECTIONS*/', json.dumps(sections, ensure_ascii=False, indent=1))
    open(out_path, 'w', encoding='utf-8', newline='\n').write(page)
    print(f'{sum(len(s["rows"]) for s in sections)} rows in {len(sections)} sections -> {out_path}')


main()
