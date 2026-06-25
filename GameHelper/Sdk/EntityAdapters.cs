// <copyright file="EntityAdapters.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Sdk
{
    using System.Collections.Generic;
    using System.Numerics;
    using ExileBridge;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameOffsets.Objects.Components;
    using RemoteEntity = GameHelper.RemoteObjects.States.InGameStateObjects.Entity;

    /// <summary>Adapter over the host's current-area entity collections.</summary>
    internal sealed class EntitiesServiceAdapter : IEntitiesService
    {
        /// <inheritdoc />
        public IReadOnlyCollection<IEntity> Awake => Wrap(
            Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values);

        /// <inheritdoc />
        public IReadOnlyCollection<IEntity> Sleeping => Wrap(
            Core.States.InGameStateObject.CurrentAreaInstance.SleepingEntities.Values);

        /// <inheritdoc />
        public bool TryGetById(uint id, out IEntity entity)
        {
            foreach (var e in Core.States.InGameStateObject.CurrentAreaInstance.AwakeEntities.Values)
            {
                if (e.Id == id)
                {
                    entity = new EntityAdapter(e);
                    return true;
                }
            }

            entity = null!;
            return false;
        }

        /// <inheritdoc />
        public void ScanSleeping(System.Func<string, bool> pathFilter, System.Action<IEntity> onMatch) =>
            Core.States.InGameStateObject.CurrentAreaInstance.ScanSleepingEntities(
                pathFilter, (_, e) => onMatch(new EntityAdapter(e)));

        private static IReadOnlyCollection<IEntity> Wrap(ICollection<RemoteEntity> source)
        {
            var list = new List<IEntity>(source.Count);
            foreach (var e in source)
            {
                list.Add(new EntityAdapter(e));
            }

            return list;
        }
    }

    /// <summary>Adapter exposing a host <see cref="RemoteEntity" /> as an SDK <see cref="IEntity" />.</summary>
    internal sealed class EntityAdapter : IEntity
    {
        private readonly RemoteEntity entity;

        internal EntityAdapter(RemoteEntity entity) => this.entity = entity;

        /// <inheritdoc />
        public nint Address => this.entity.Address;

        /// <inheritdoc />
        public uint Id => this.entity.Id;

        /// <inheritdoc />
        public string Path => this.entity.Path;

        /// <inheritdoc />
        public bool IsValid => this.entity.IsValid;

        /// <inheritdoc />
        public EntityType Type => this.entity.EntityType switch
        {
            EntityTypes.Chest => EntityType.Chest,
            EntityTypes.NPC => EntityType.Npc,
            EntityTypes.Player => EntityType.Player,
            EntityTypes.Shrine => EntityType.Shrine,
            EntityTypes.Monster => EntityType.Monster,
            EntityTypes.DeliriumBomb => EntityType.DeliriumBomb,
            EntityTypes.DeliriumSpawner => EntityType.DeliriumSpawner,
            EntityTypes.OtherImportantObjects => EntityType.OtherImportantObjects,
            EntityTypes.Item => EntityType.Item,
            EntityTypes.Renderable => EntityType.Renderable,
            _ => EntityType.Unidentified,
        };

        /// <inheritdoc />
        public EntitySubtype Subtype => this.entity.EntitySubtype switch
        {
            EntitySubtypes.None => EntitySubtype.None,
            EntitySubtypes.PlayerSelf => EntitySubtype.PlayerSelf,
            EntitySubtypes.PlayerOther => EntitySubtype.PlayerOther,
            EntitySubtypes.ChestWithMagicRarity => EntitySubtype.ChestWithMagicRarity,
            EntitySubtypes.ChestWithRareRarity => EntitySubtype.ChestWithRareRarity,
            EntitySubtypes.ExpeditionChest => EntitySubtype.ExpeditionChest,
            EntitySubtypes.BreachChest => EntitySubtype.BreachChest,
            EntitySubtypes.Strongbox => EntitySubtype.Strongbox,
            EntitySubtypes.SpecialNPC => EntitySubtype.SpecialNpc,
            EntitySubtypes.POIMonster => EntitySubtype.PoiMonster,
            EntitySubtypes.PinnacleBoss => EntitySubtype.PinnacleBoss,
            EntitySubtypes.WorldItem => EntitySubtype.WorldItem,
            EntitySubtypes.InventoryItem => EntitySubtype.InventoryItem,
            _ => EntitySubtype.Unidentified,
        };

        /// <inheritdoc />
        public EntityState State => this.entity.EntityState switch
        {
            EntityStates.Useless => EntityState.Useless,
            EntityStates.PlayerLeader => EntityState.PlayerLeader,
            EntityStates.MonsterFriendly => EntityState.MonsterFriendly,
            EntityStates.PinnacleBossHidden => EntityState.PinnacleBossHidden,
            _ => EntityState.None,
        };

        /// <inheritdoc />
        public int CustomGroup => this.entity.EntityCustomGroup;

        /// <inheritdoc />
        public Vector2 GridPosition =>
            this.entity.TryGetComponent<Render>(out var render)
                ? new Vector2(render.GridPosition.X, render.GridPosition.Y)
                : Vector2.Zero;

        /// <inheritdoc />
        public bool TryGetComponent<TComponent>(out TComponent component)
            where TComponent : class, IComponent
        {
            component = null!;

            if (typeof(TComponent) == typeof(ILife))
            {
                if (this.entity.TryGetComponent<Life>(out var life))
                {
                    component = (TComponent)(IComponent)new LifeAdapter(life);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IPositioned))
            {
                if (this.entity.TryGetComponent<Positioned>(out var positioned))
                {
                    component = (TComponent)(IComponent)new PositionedAdapter(positioned);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IRender))
            {
                if (this.entity.TryGetComponent<Render>(out var render))
                {
                    component = (TComponent)(IComponent)new RenderAdapter(render);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IStats))
            {
                if (this.entity.TryGetComponent<Stats>(out var stats))
                {
                    component = (TComponent)(IComponent)new StatsAdapter(stats);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IObjectMagicProperties))
            {
                if (this.entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    component = (TComponent)(IComponent)new ObjectMagicPropertiesAdapter(omp);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IPlayer))
            {
                if (this.entity.TryGetComponent<Player>(out var player))
                {
                    component = (TComponent)(IComponent)new PlayerAdapter(player);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IShrine))
            {
                if (this.entity.TryGetComponent<Shrine>(out var shrine))
                {
                    component = (TComponent)(IComponent)new ShrineAdapter(shrine);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(ITargetable))
            {
                if (this.entity.TryGetComponent<Targetable>(out var targetable))
                {
                    component = (TComponent)(IComponent)new TargetableAdapter(targetable);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IMinimapIcon))
            {
                if (this.entity.TryGetComponent<MinimapIcon>(out var icon))
                {
                    component = (TComponent)(IComponent)new MinimapIconAdapter(icon);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IStateMachine))
            {
                if (this.entity.TryGetComponent<StateMachine>(out var machine))
                {
                    component = (TComponent)(IComponent)new StateMachineAdapter(machine);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(ITriggerableBlockage))
            {
                if (this.entity.TryGetComponent<TriggerableBlockage>(out var blockage))
                {
                    component = (TComponent)(IComponent)new TriggerableBlockageAdapter(blockage);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IBuffs))
            {
                if (this.entity.TryGetComponent<Buffs>(out var buffs))
                {
                    component = (TComponent)(IComponent)new BuffsAdapter(buffs);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IChest))
            {
                if (this.entity.TryGetComponent<Chest>(out var chest))
                {
                    component = (TComponent)(IComponent)new ChestAdapter(chest);
                    return true;
                }

                return false;
            }

            if (typeof(TComponent) == typeof(IGroundItem))
            {
                if (this.entity.TryGetComponent<WorldItem>(out var worldItem))
                {
                    component = (TComponent)(IComponent)new GroundItemAdapter(worldItem);
                    return true;
                }

                return false;
            }

            return false;
        }
    }

    /// <summary>Adapter exposing a host <see cref="VitalStruct" /> as an SDK <see cref="IVital" />.</summary>
    internal sealed class VitalAdapter : IVital
    {
        private readonly VitalStruct vital;

        internal VitalAdapter(VitalStruct vital) => this.vital = vital;

        /// <inheritdoc />
        public int Current => this.vital.Current;

        /// <inheritdoc />
        public int Total => this.vital.Total;

        /// <inheritdoc />
        public int Unreserved => this.vital.Unreserved;

        /// <inheritdoc />
        public int ReservedFlat => this.vital.ReservedFlat;

        /// <inheritdoc />
        public int ReservedPercent => this.vital.ReservedPercent;

        /// <inheritdoc />
        public float Regeneration => this.vital.Regeneration;

        /// <inheritdoc />
        public int CurrentInPercent => this.vital.CurrentInPercent();
    }

    /// <summary>Adapter for the <see cref="Life" /> component.</summary>
    internal sealed class LifeAdapter : ILife
    {
        private readonly Life life;

        internal LifeAdapter(Life life) => this.life = life;

        /// <inheritdoc />
        public bool IsAlive => this.life.IsAlive;

        /// <inheritdoc />
        public IVital Health => new VitalAdapter(this.life.Health);

        /// <inheritdoc />
        public IVital EnergyShield => new VitalAdapter(this.life.EnergyShield);

        /// <inheritdoc />
        public IVital Mana => new VitalAdapter(this.life.Mana);

        /// <inheritdoc />
        public IVital Ward => new VitalAdapter(this.life.Ward);

        /// <inheritdoc />
        public IVital Divinity => new VitalAdapter(this.life.Divinity);
    }

    /// <summary>Adapter for the <see cref="Stats" /> component.</summary>
    internal sealed class StatsAdapter : IStats
    {
        private readonly Stats stats;

        internal StatsAdapter(Stats stats) => this.stats = stats;

        /// <inheritdoc />
        public bool TryGetItemStat(GameStat stat, out int value)
        {
            value = 0;
            return this.stats.StatsChangedByItems != null &&
                   this.stats.StatsChangedByItems.TryGetValue(Map(stat), out value);
        }

        /// <inheritdoc />
        public int GetTotalStat(GameStat stat)
        {
            var key = Map(stat);
            var total = 0;
            if (this.stats.StatsChangedByItems != null &&
                this.stats.StatsChangedByItems.TryGetValue(key, out var a))
            {
                total += a;
            }

            if (this.stats.StatsChangedByBuffAndActions != null &&
                this.stats.StatsChangedByBuffAndActions.TryGetValue(key, out var b))
            {
                total += b;
            }

            return total;
        }

        private static GameStats Map(GameStat stat) => stat switch
        {
            GameStat.EvasionRating => GameStats.evasion_rating,
            GameStat.MaximumEnergyShield => GameStats.maximum_energy_shield,
            GameStat.Armour => GameStats.armour,
            GameStat.MaximumLife => GameStats.maximum_life,
            GameStat.MovementSpeedFromEvasion =>
                GameStats.movement_speed_is_only_base_positive_1percentage_per_x_evasion_rating,
            GameStat.MonsterInsideMonolith => GameStats.monster_inside_monolith,
            _ => default,
        };
    }

    /// <summary>Adapter for the <see cref="ObjectMagicProperties" /> component.</summary>
    internal sealed class ObjectMagicPropertiesAdapter : IObjectMagicProperties
    {
        private readonly ObjectMagicProperties omp;

        internal ObjectMagicPropertiesAdapter(ObjectMagicProperties omp) => this.omp = omp;

        /// <inheritdoc />
        public ExileBridge.Rarity Rarity => this.omp.Rarity switch
        {
            GameHelper.RemoteEnums.Rarity.Magic => ExileBridge.Rarity.Magic,
            GameHelper.RemoteEnums.Rarity.Rare => ExileBridge.Rarity.Rare,
            GameHelper.RemoteEnums.Rarity.Unique => ExileBridge.Rarity.Unique,
            _ => ExileBridge.Rarity.Normal,
        };

        /// <inheritdoc />
        public IReadOnlyCollection<string> ModNames => this.omp.ModNames;
    }

    /// <summary>Adapter for the <see cref="Positioned" /> component.</summary>
    internal sealed class PositionedAdapter : IPositioned
    {
        private readonly Positioned positioned;

        internal PositionedAdapter(Positioned positioned) => this.positioned = positioned;

        /// <inheritdoc />
        public bool IsFriendly => this.positioned.IsFriendly;
    }

    /// <summary>Adapter for the <see cref="Render" /> component.</summary>
    internal sealed class RenderAdapter : IRender
    {
        private readonly Render render;

        internal RenderAdapter(Render render) => this.render = render;

        /// <inheritdoc />
        public Vector2 GridPosition => new(this.render.GridPosition.X, this.render.GridPosition.Y);

        /// <inheritdoc />
        public Vector3 WorldPosition => new(this.render.WorldPosition.X, this.render.WorldPosition.Y, this.render.WorldPosition.Z);

        /// <inheritdoc />
        public Vector3 ModelBounds => new(this.render.ModelBounds.X, this.render.ModelBounds.Y, this.render.ModelBounds.Z);

        /// <inheritdoc />
        public float TerrainHeight => this.render.TerrainHeight;
    }
}
