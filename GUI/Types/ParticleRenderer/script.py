from pathlib import Path

# for every .cs file in this directory and subdirectories

find = r"""if (parse.Data.ContainsKey("{memberName}"))
{
    {member} = parse.Data.GetNumberProvider("{memberName}");
}"""

#replace = r"{member} = parse.NumberProvider("{memberName}")"

def trim_contains_key(cs, content):

    trim_next_4_with: dict[int, str] = {}

    for i in range(len(content) - 3):
        group_of_4 = content[i:i+4]
        if "if (parse.Data.ContainsKey" in group_of_4[0]:
            memberNameCpp = group_of_4[0].split('"')[1]
            memberNameCs = group_of_4[2].split('=')[0].strip()

            func_with_generic = group_of_4[2].split('=')[1].split('(')[0].strip()
            func = func_with_generic.split('<')[0]
            generic = func_with_generic.removeprefix(func)

            indentation = group_of_4[0].split('i')[0]

            if (group_of_4[3].strip() != "}"):
                continue

            new_dict = {
                "parse.Data.GetNumberProvider": "parse.NumberProvider",
                "parse.Data.GetInt32Property": "parse.Int32",
                "parse.Data.GetIntegerProperty": "parse.Int32",
                "parse.Data.GetProperty<bool>": "parse.Boolean",
                "parse.Data.GetFloatProperty": "parse.Float",
                "parse.Data.GetVectorProvider": "parse.VectorProvider",
                #"parse.Data.GetEnumValue": "parse.Enum",
            }

            if neww:=new_dict.get(func):
                trim_next_4_with[i] = indentation + f"{memberNameCs} = {neww}{generic}(\"{memberNameCpp}\", {memberNameCs});\n"

            # just removes the ContainsKey
            if func == "parse.VectorProvider":
                trim_next_4_with[i] = indentation + f"{memberNameCs} = {func}{generic}(\"{memberNameCpp}\", {memberNameCs});\n"
    
    with open(cs, "w") as f:
        trimming = 0
        for i, line in enumerate(content):
            if trimming > 0:
                trimming -= 1
                continue

            if (new:=trim_next_4_with.get(i)) is not None:
                f.write(new)
                trimming = 3
                continue
            f.write(line)

def trim_blank_lines(cs, content):
    marker: set[int] = set()

    for i in range(len(content) - 2):
        group_of_3 = content[i:i+3]
        if "= parse." in group_of_3[0] \
            and group_of_3[1].strip() == "" \
            and ("= parse." in group_of_3[2] or "}" in group_of_3[2]):
            marker.add(i+1)

    with open(cs, "w") as f:
        for i, line in enumerate(content):
            if i in marker:
                continue
            f.write(line)

for cs in Path(".").rglob("*.cs"):
    with open(cs, "r") as f:
        content = f.readlines()

    if len(content) < 3:
        continue

    trim_contains_key(cs, content)
    trim_blank_lines(cs, content)

