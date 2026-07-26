#!/usr/bin/env python3
"""Analyse a --shader_dump_all dump, and verify a reconstructed .slang against it.

Two modes, both driven by the manifest.tsv that --shader_dump_all writes:

  analyse   Which combos actually change the emitted code, and the smallest set of variants
            that has to be read to understand the shader.

  check     Preprocess a reconstruction once per combo configuration and confirm each feature
            appears in exactly the variants the real bytecode has it in.

See .claude/skills/shader-reconstruct/SKILL.md for how this fits the whole workflow.
"""

import argparse
import collections
import csv
import io
import itertools
import json
import os
import re
import sys


def read_manifest(dump_dir):
    path = os.path.join(dump_dir, "manifest.tsv")
    with io.open(path, encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle, delimiter="\t"))
    if not rows:
        sys.exit(f"{path} has no rows")
    return rows


def parse_values(text):
    """'S_ALPHA_TEST, D_BLEND_WEIGHT_COUNT=4' -> {'S_ALPHA_TEST': 1, 'D_BLEND_WEIGHT_COUNT': 4}"""
    values = {}
    for part in (p.strip() for p in text.split(",")):
        if not part:
            continue
        if "=" in part:
            name, value = part.split("=", 1)
            values[name.strip()] = int(value)
        else:
            values[part] = 1
    return values


def combo_config(row, names):
    """Full combo assignment for a manifest row, zero for anything not listed."""
    config = dict.fromkeys(names, 0)
    config.update(parse_values(row["static_values"]))
    config.update(parse_values(row["dynamic_values"]))
    return config


def all_combo_names(rows):
    names = []
    for row in rows:
        for key in itertools.chain(parse_values(row["static_values"]), parse_values(row["dynamic_values"])):
            if key not in names:
                names.append(key)
    return names


def strip_comments(text):
    return "\n".join(re.sub(r"//.*$", "", line) for line in text.splitlines())


def analyse(dump_dir):
    rows = read_manifest(dump_dir)
    names = all_combo_names(rows)
    configs = {}

    for row in rows:
        configs[tuple(sorted(combo_config(row, names).items()))] = row["file"]

    files = set(configs.values())
    print(f"{len(rows)} variants, {len(files)} unique files, {len(names)} combos\n")

    # A combo matters if some pair of configurations differing only in it maps to different files.
    print("combo                                  values seen  changes the code")
    inert = []
    for name in names:
        seen = sorted({dict(k)[name] for k in configs})
        differs = False
        for key, file in configs.items():
            config = dict(key)
            for value in seen:
                if value == config[name]:
                    continue
                other = dict(config)
                other[name] = value
                other_file = configs.get(tuple(sorted(other.items())))
                if other_file is not None and other_file != file:
                    differs = True
                    break
            if differs:
                break
        print(f"  {name:<38} {str(seen):<12} {'yes' if differs else 'NO - inert'}")
        if not differs:
            inert.append(name)

    if inert:
        print(f"\nInert combos select identical bytecode everywhere: {', '.join(inert)}")
        print("Confirm the render state matches too before calling them no-ops.")

    # The smallest useful reading list: the all-zero variant, then one variant per single combo raised.
    print("\nSuggested reading order (base first, then one combo at a time):")
    base_key = tuple(sorted(dict.fromkeys(names, 0).items()))
    base = configs.get(base_key)
    if base:
        print(f"  {'<base, all zero>':<52} {base}")
    for name in names:
        if name in inert:
            continue
        for value in sorted({dict(k)[name] for k in configs} - {0}):
            config = dict.fromkeys(names, 0)
            config[name] = value
            file = configs.get(tuple(sorted(config.items())))
            if file and file != base:
                print(f"  {name + '=' + str(value):<52} {file}")

    unreachable = len(files) - len({f for f in configs.values()})
    if unreachable:
        print(f"\n{unreachable} files unaccounted for")


def evaluate(expr, env):
    text = expr
    for name in sorted(env, key=len, reverse=True):
        text = re.sub(r"\b%s\b" % re.escape(name), str(env[name]), text)
    text = text.replace("&&", " and ").replace("||", " or ").replace("!", " not ")

    # Any identifier still standing after substitution was never defined, which the C preprocessor
    # treats as 0. Matching that catches a macro used in an #if placed above its own #define.
    text = re.sub(r"\b(?!and\b|or\b|not\b|True\b|False\b)[A-Za-z_]\w*\b", "0", text)

    try:
        return bool(eval(text))  # noqa: S307 - the expression comes from the local reconstruction
    except Exception as error:
        sys.exit(f"cannot evaluate #if {expr!r} -> {text!r}: {error}")


def preprocess(lines, defines):
    """Minimal #if/#elif/#else/#endif expansion. #defines in the file are ignored in favour of `defines`."""
    env = dict(defines)
    out, stack, taken = [], [], []

    for line in lines:
        stripped = line.strip()
        match = re.match(r"#(if|elif|else|endif)\b\s*(.*)", stripped)
        if match:
            kind, expr = match.group(1), match.group(2)
            if kind == "if":
                value = evaluate(expr, env) if all(stack) else False
                stack.append(value)
                taken.append(value)
            elif kind == "elif":
                value = (not taken[-1]) and evaluate(expr, env) if all(stack[:-1]) else False
                stack[-1] = value
                taken[-1] = taken[-1] or value
            elif kind == "else":
                stack[-1] = (not taken[-1]) if all(stack[:-1]) else False
            else:
                stack.pop()
                taken.pop()
            continue

        if stripped.startswith("#define"):
            # Derived helper defines (#define _NEEDS_UV (A || B)) still need to be in scope.
            parts = stripped.split(None, 2)
            if len(parts) == 3 and parts[1] not in env:
                env[parts[1]] = int(evaluate(parts[2].split("//")[0].strip(), env))
            continue

        if all(stack):
            out.append(line)

    if stack:
        sys.exit("unbalanced #if in the reconstruction")

    return strip_comments("\n".join(out))


def check(dump_dir, reconstruction_path, markers_path):
    rows = read_manifest(dump_dir)
    names = all_combo_names(rows)

    with io.open(reconstruction_path, encoding="utf-8") as handle:
        lines = handle.read().splitlines()
    with io.open(markers_path, encoding="utf-8") as handle:
        markers = json.load(handle)

    cache, seen = {}, set()
    failures = collections.Counter()

    for row in rows:
        config = combo_config(row, names)
        key = tuple(sorted(config.items()))
        if key in seen:
            continue
        seen.add(key)

        if row["file"] not in cache:
            with io.open(os.path.join(dump_dir, row["file"]), encoding="utf-8") as handle:
                cache[row["file"]] = strip_comments(handle.read())

        real = cache[row["file"]]
        mine = preprocess(lines, config)

        for in_variant, in_reconstruction in markers.items():
            if (in_variant in real) != (in_reconstruction in mine):
                failures[in_variant] += 1
                if failures[in_variant] <= 2:
                    print(
                        f"MISMATCH {in_variant!r}: variant={in_variant in real} "
                        f"reconstruction={in_reconstruction in mine} :: "
                        f"[{row['static_values']}] [{row['dynamic_values']}]"
                    )

    print(f"\n{len(seen)} combo configurations preprocessed, {len(markers)} markers each")
    print("mismatches:", dict(failures) if failures else "none")
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dump_dir", help="folder --shader_dump_all wrote, the one holding manifest.tsv")
    parser.add_argument("--reconstruction", help="the merged .slang to verify")
    parser.add_argument("--markers", help='json of {"text in the dumped variant": "text in the reconstruction"}')
    args = parser.parse_args()

    if args.reconstruction or args.markers:
        if not (args.reconstruction and args.markers):
            parser.error("--reconstruction and --markers go together")
        sys.exit(check(args.dump_dir, args.reconstruction, args.markers))

    analyse(args.dump_dir)


if __name__ == "__main__":
    main()
