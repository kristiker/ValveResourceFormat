using System.Globalization;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// A live entity in the scene: the compiled keyvalues plus whatever runtime state its class needs.
/// Subclasses implement class-specific behaviour by overriding <see cref="AcceptInput"/> and
/// <see cref="Think"/>; everything else is driven by the <see cref="EntityWorld"/> that owns it.
/// </summary>
public abstract class EntityInstance
{
    /// <summary>
    /// An empty keyvalue set, so entities with no map data behind them — the player — still read
    /// keys like any other.
    /// </summary>
    private static readonly EntityLump.Entity NoKeyValues = new() { ParentLump = new EntityLump { Resource = null! } };

    /// <summary>Gets the compiled keyvalues this entity was spawned from.</summary>
    public EntityLump.Entity Data { get; private set; } = NoKeyValues;

    /// <summary>Gets the entity's <c>classname</c>.</summary>
    public string Classname { get; private set; } = string.Empty;

    /// <summary>Gets the entity's <c>targetname</c>, or <see langword="null"/> when it is unnamed.</summary>
    public string? TargetName { get; private set; }

    /// <summary>Gets the world containing this entity. Assigned when the entity is added.</summary>
    public EntityWorld World { get; internal set; } = null!;

    /// <summary>Gets the outputs this entity fires.</summary>
    public List<EntityConnection> Connections { get; } = [];

    /// <summary>Gets the entity's compiled <c>origin</c>, before any <see cref="ParentTransform"/>.</summary>
    public Vector3 LocalOrigin { get; private set; }

    /// <summary>
    /// Gets the entity's compiled <c>angles</c> as Euler pitch/yaw/roll in degrees. Rotating movers
    /// work in this space, adding their sweep to one component the way the engine does.
    /// </summary>
    public Vector3 LocalAngles { get; private set; }

    /// <summary>
    /// Gets the transform a <c>point_template</c> imposes on this entity, or the identity for
    /// entities placed directly in the map.
    /// </summary>
    public Matrix4x4 ParentTransform { get; private set; } = Matrix4x4.Identity;

    /// <summary>
    /// Copies the compiled keyvalues onto a freshly constructed entity. Called only by
    /// <see cref="EntityFactory"/>, which is the single way entities come into being.
    /// </summary>
    /// <param name="data">The compiled keyvalues, or <see langword="null"/> for the player entity.</param>
    /// <param name="classname">The entity's classname.</param>
    /// <param name="parentTransform">Transform imposed by a containing <c>point_template</c>.</param>
    internal void Initialize(EntityLump.Entity? data, string classname, Matrix4x4 parentTransform)
    {
        Classname = classname;
        ParentTransform = parentTransform;

        if (data == null)
        {
            return;
        }

        Data = data;

        var targetName = data.GetStringProperty("targetname");
        TargetName = string.IsNullOrEmpty(targetName) ? null : targetName;

        LocalOrigin = data.GetVector3Property("origin");
        LocalAngles = data.GetVector3Property("angles");

        Connections.AddRange(EntityConnection.Parse(data.Connections));
    }

    /// <summary>
    /// Gets the transform the entity was compiled at. Movers offset from this rather than
    /// accumulating, so their motion never drifts.
    /// </summary>
    public Matrix4x4 SpawnTransform => BuildTransform(LocalOrigin, LocalAngles);

    /// <summary>Gets the entity's spawn origin in world space.</summary>
    public Vector3 SpawnOrigin => Vector3.Transform(LocalOrigin, ParentTransform);

    /// <summary>
    /// Gets the entity's spawn orientation as Euler pitch/yaw/roll, in degrees. Equal to
    /// <see cref="LocalAngles"/> outside of templates.
    /// </summary>
    public Vector3 SpawnAngles => LocalAngles;

    /// <summary>
    /// Composes a world transform from an origin and Euler angles in this entity's local space.
    /// </summary>
    /// <param name="origin">Local origin.</param>
    /// <param name="angles">Local Euler pitch/yaw/roll in degrees.</param>
    /// <returns>The world transform.</returns>
    protected Matrix4x4 BuildTransform(Vector3 origin, Vector3 angles)
        => EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles)
            * Matrix4x4.CreateTranslation(origin)
            * ParentTransform;

    /// <summary>
    /// Gets or sets a value indicating whether the entity is active. What this means is class-specific:
    /// a disabled trigger stops touching, a disabled <c>func_brush</c> stops colliding.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets the renderable nodes that follow this entity's transform. Empty for point entities.</summary>
    public List<SceneNode> VisualNodes { get; } = [];

    /// <summary>
    /// Called once after every entity in the map has been registered, so spawn code may resolve
    /// other entities by name.
    /// </summary>
    public virtual void Spawn()
    {
    }

    /// <summary>
    /// Advances class-specific simulation. Only called for entities that set <see cref="WantsThink"/>.
    /// </summary>
    /// <param name="deltaTime">Seconds since the last tick.</param>
    public virtual void Think(float deltaTime)
    {
    }

    /// <summary>Gets a value indicating whether <see cref="Think"/> should be called every tick.</summary>
    public virtual bool WantsThink => false;

    /// <summary>
    /// Handles an input fired at this entity.
    /// </summary>
    /// <param name="input">The input name, matched case-insensitively.</param>
    /// <param name="parameter">The input parameter, empty when none was supplied.</param>
    /// <param name="activator">The entity that started the I/O chain, usually the player.</param>
    /// <param name="caller">The entity whose output fired this input.</param>
    /// <returns><see langword="true"/> if the input was recognised.</returns>
    public virtual bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Enable"))
        {
            Enabled = true;
            return true;
        }

        if (InputIs(input, "Disable"))
        {
            Enabled = false;
            return true;
        }

        if (InputIs(input, "Toggle"))
        {
            Enabled = !Enabled;
            return true;
        }

        if (InputIs(input, "FireUser1") || InputIs(input, "FireUser2") || InputIs(input, "FireUser3") || InputIs(input, "FireUser4"))
        {
            SendOutput(string.Concat("OnUser", input.AsSpan("FireUser".Length)), activator);
            return true;
        }

        return false;
    }

    /// <summary>Compares an input or output name the way the engine does, ignoring case.</summary>
    /// <param name="input">The received name.</param>
    /// <param name="name">The name to compare against.</param>
    /// <returns><see langword="true"/> when the names match.</returns>
    protected static bool InputIs(string input, string name)
        => string.Equals(input, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fires one of this entity's outputs, queuing every connection listening for it.
    /// </summary>
    /// <param name="output">The output name.</param>
    /// <param name="activator">The entity that caused the output, propagated down the chain.</param>
    protected void SendOutput(string output, EntityInstance? activator)
        => World?.SendOutput(this, output, activator);

    /// <summary>Reads a float keyvalue, falling back when it is absent.</summary>
    /// <param name="key">The keyvalue name.</param>
    /// <param name="defaultValue">Value used when the key is missing.</param>
    /// <returns>The keyvalue, or <paramref name="defaultValue"/>.</returns>
    protected float GetFloat(string key, float defaultValue = 0f)
    {
        if (!Data.TryGetValue(key, out var value) || value == null || value.ValueType == KVValueType.Null)
        {
            return defaultValue;
        }

        if (value.ValueType == KVValueType.String)
        {
            return float.TryParse((string)value, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        return value.ValueType is KVValueType.Collection or KVValueType.Array or KVValueType.BinaryBlob
            ? defaultValue
            : value.ToSingle(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads an int keyvalue, falling back when it is absent.</summary>
    /// <param name="key">The keyvalue name.</param>
    /// <param name="defaultValue">Value used when the key is missing.</param>
    /// <returns>The keyvalue, or <paramref name="defaultValue"/>.</returns>
    protected int GetInt(string key, int defaultValue = 0)
    {
        if (!Data.TryGetValue(key, out var value) || value == null || value.ValueType == KVValueType.Null)
        {
            return defaultValue;
        }

        if (value.ValueType == KVValueType.String)
        {
            return int.TryParse((string)value, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        return value.ValueType is KVValueType.Collection or KVValueType.Array or KVValueType.BinaryBlob
            ? defaultValue
            : value.ToInt32(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a bool keyvalue. Compiled maps store these either as real booleans or as
    /// <c>"0"</c>/<c>"1"</c> strings depending on the field, so both are accepted.
    /// </summary>
    /// <param name="key">The keyvalue name.</param>
    /// <param name="defaultValue">Value used when the key is missing.</param>
    /// <returns>The keyvalue, or <paramref name="defaultValue"/>.</returns>
    protected bool GetBool(string key, bool defaultValue = false)
    {
        if (!Data.TryGetValue(key, out var value) || value == null || value.ValueType == KVValueType.Null)
        {
            return defaultValue;
        }

        if (value.ValueType == KVValueType.String)
        {
            // Enum-backed fields ("solidity", "inputfilter", ...) compile to numeric strings
            var text = (string)value;
            return text.Length > 0 && !string.Equals(text, "0", StringComparison.Ordinal) && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
        }

        if (value.ValueType is KVValueType.Collection or KVValueType.Array or KVValueType.BinaryBlob)
        {
            return defaultValue;
        }

        return value.ToBoolean(CultureInfo.InvariantCulture);
    }

    /// <summary>Gets this entity's spawnflags.</summary>
    protected int SpawnFlags => GetInt("spawnflags");

    /// <summary>Tests a spawnflag bit.</summary>
    /// <param name="flag">The bit mask to test.</param>
    /// <returns><see langword="true"/> when the bit is set.</returns>
    protected bool HasSpawnFlag(int flag) => (SpawnFlags & flag) != 0;

    /// <summary>Returns a debug description of the entity.</summary>
    /// <returns>The classname and targetname.</returns>
    public override string ToString()
        => TargetName == null ? Classname : string.Concat(Classname, " \"", TargetName, "\"");
}
