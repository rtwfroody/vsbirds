using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using System;
using Vintagestory.API;
using Vintagestory.API.Common.Entities;

[assembly: ModInfo( "Birds",
    Description = "Make the world feel more alive by adding birds!",
    Website     = "https://github.com/rtwfroody/vsbirds",
    Authors     = new []{ "Tim Newsome <tim@casualhacker.net>" } )]

namespace Birds
{
    public class BirdMod : ModSystem
    {
        static BirdMod()
        {
            Vintagestory.GameContent.AiTaskRegistry.Register<AiTaskPerch>("perch");
        }

        public override void Start(ICoreAPI api)
        {
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
        }
    }

    public class AiTaskPerch : Vintagestory.API.Common.AiTaskBase
    {
        // Configuration parameters.
        int searchRadius;
        int minDistance;
        int minDuration, maxDuration;
        double flightSpeed;

        ICachingBlockAccessor blockAccess;

        EntityPos previousPos;
        BlockPos bestPerch;
        float bestPerchScore;
        Vec3d startPos;

        Vintagestory.GameContent.EntityPartitioning partitionUtil;

        Vec3d target;
        Vec3d waypoint;

        public AiTaskPerch(EntityAgent entity) : base(entity)
        {
            blockAccess = ((ICoreServerAPI) entity.Api).WorldManager.GetCachingBlockAccessor(true, true);
        }

        public override void LoadConfig(JsonObject taskConfig, JsonObject aiConfig)
        {
            base.LoadConfig(taskConfig, aiConfig);

            partitionUtil = entity.Api.ModLoader.GetModSystem<Vintagestory.GameContent.EntityPartitioning>();

            searchRadius = taskConfig["searchRadius"].AsInt(32);
            minDistance = taskConfig["minDistance"].AsInt(12);
            minDuration = taskConfig["minDuration"].AsInt(5000);
            maxDuration = taskConfig["maxDuration"].AsInt(60000);
            flightSpeed = taskConfig["flightSpeed"].AsDouble(.03);
        }

        public int square(int n)
        {
            return n * n;
        }

        public override bool ShouldExecute()
        {
            return entity.World.ElapsedMilliseconds > cooldownUntilMs;
        }

        float PerchScore(BlockPos p)
        {
            float score = 0;
            int[,] offsets = new int[,]
            {
                { -1, -1 },
                { -1, 0 },
                { -1, 1 },
                { 0, -1 },
                { 0, 1},
                { 1, -1 },
                { 1, 0 },
                { 1, 1 }
            };

            int heightHere = entity.World.BlockAccessor.GetRainMapHeightAt(p);
            for (int i = 0; i < offsets.GetLength(0); i++)
            {
                BlockPos neighbor = new BlockPos(p.X + offsets[i, 0], p.Y, p.Z + offsets[i, 1]);
                score += heightHere - entity.World.BlockAccessor.GetRainMapHeightAt(neighbor);
            }

            return score;
        }

        void FindBetterPerch()
        {
            // Pick a random spot in front of us.
            double distance = 8 + entity.World.Rand.NextDouble() * (32 - 8);
            double yaw = entity.ServerPos.Yaw + Math.PI / 3 + entity.World.Rand.NextDouble() * Math.PI / 3;
            Vec3d spot = entity.ServerPos.XYZ.AheadCopy(distance, 0, yaw);
            BlockPos p = new BlockPos((int)spot.X, (int)spot.Y, (int)spot.Z);
            p.Y = entity.World.BlockAccessor.GetRainMapHeightAt(p) + 1;

            float score = PerchScore(p);

            if (bestPerch == null || score > bestPerchScore)
            {
                bestPerch = p;
                bestPerchScore = score;
                entity.World.Logger.Debug($"New best perch with score {bestPerchScore}: {bestPerch}");
                target = new Vec3d(bestPerch.X + 0.5, bestPerch.Y, bestPerch.Z + 0.5);
            }
        }

        public override void StartExecute()
        {
            entity.World.Logger.Debug("AiTaskPerch.StartExecute()");

            base.StartExecute();

            bestPerch = null;
            startPos = entity.ServerPos.XYZ;

            target = entity.ServerPos.XYZ.AheadCopy(64, 0, entity.World.Rand.NextDouble() * Math.PI * 2);
            waypoint = null;

            entity.World.Logger.Debug($"target={Fmt(target)}");
        }

        static double VecDistance(Vec3d a, Vec3d b)
        {
            Vec3d delta = b.Clone();
            delta.Sub(a);
            return delta.Length();
        }

        void DebugParticles(Vec3d position, byte r, byte g, byte b)
        {
#if DEBUG
            SimpleParticleProperties particles = new SimpleParticleProperties(
                    10, 10, ColorUtil.ColorFromRgba(b, g, r, 50),
                    position, new Vec3d(position.X, position.Y + 1, position.Z), new Vec3f(-1, -1, -1), new Vec3f(1, 1, 1));
            entity.World.SpawnParticles(particles);
#endif
        }

        /* Avoid collisions with blocks up to distance ahead. */
        bool AvoidCollision(float distance)
        {
            const float collisionStep = 0.5f;
            float collisionDistance;
            Cuboidf cb = entity.CollisionBox;
            entity.World.Logger.Debug($"collisionBox={cb.X1}/{cb.Y1}/{cb.Z1} -- {cb.X2}/{cb.Y2}/{cb.Z2}");
            for (collisionDistance = 0; collisionDistance < distance; collisionDistance += collisionStep)
            {
                if (entity.World.CollisionTester.IsColliding(blockAccess, entity.CollisionBox,
                        entity.ServerPos.AheadCopy(collisionDistance).XYZ))
                    break;
            }

            entity.World.Logger.Debug($"collisionDistance={collisionDistance} distance={distance}");

            if (collisionDistance < distance)
            {
                // find some open air and go there
                DebugParticles(entity.ServerPos.XYZ, 255, 100, 100);
                const float minScore = -1000;
                (float score, float pitch, float yaw) bestSolution = (minScore, 0, 0);
                for (float yaw = 0; yaw < 2*Math.PI; yaw += (float) Math.PI/4)
                {
                    // Vary between 45 degrees down and up.
                    for (float pitch = -(float) Math.PI / 2; pitch <= Math.PI / 2; pitch += (float) Math.PI/4)
                    {
                        float d;
                        // Don't start at 0, because we might be just barely in a collision already.
                        for (d = collisionStep; d < 4; d += collisionStep)
                        {
                            Vec3d pos = entity.ServerPos.XYZ.AheadCopy(d, pitch, yaw);
                            if (entity.World.CollisionTester.IsColliding(blockAccess, entity.CollisionBox, pos))
                                break;
                            float distanceToTarget = (float)VecDistance(target, pos);
                            // Don't pick a waypoint too close to the target, or else we won't have
                            // the turn radius to make it to the target once we get there.
                            if (distanceToTarget > 2)
                            {
                                float score = -(float)distanceToTarget;
                                if (score > bestSolution.score)
                                    bestSolution = (score: score, pitch: pitch, yaw: yaw);
                            }
                        }
                    }
                }
                entity.World.Logger.Debug($"bestSolution={bestSolution}");
                if (bestSolution.score > minScore)
                {
                    waypoint = entity.ServerPos.XYZ.AheadCopy(2, bestSolution.pitch, bestSolution.yaw);

                    if (collisionDistance < 1.5)
                    {
                        // Take drastic action to avoid imminent collision.
                        entity.ServerPos.Pitch = bestSolution.pitch;
                        entity.ServerPos.Yaw = bestSolution.yaw;
                        entity.ServerPos.Roll = 0;
                        entity.Controls.FlyVector.Set(0, 0, 0);
                        entity.Controls.FlyVector.Ahead(flightSpeed / 2, entity.ServerPos.Pitch, entity.ServerPos.Yaw + Math.PI / 2);
                        if (collisionDistance <= 0)
                        {
                            // We might be completely stuck. Warp a little.
                            entity.ServerPos.Y += .1;
                        }
                        return true;
                    }
                }
                else
                {
                    // No solution found. We're probably stuck inside a block. Die a little.
                    /*entity.ReceiveDamage(
                        new DamageSource()
                        {
                            Source = EnumDamageSource.Block,
                            Type = EnumDamageType.Crushing
                        }, 1);*/
                    DebugParticles(entity.ServerPos.XYZ, 255, 0, 0);
                }
            }

            return false;
        }

        public string Fmt(EntityPos p)
        {
            if (p == null)
                return "(null)";
            return $"(XYZ={p.X:F2}/{p.Y:F2}/{p.Z:F2} YPR={p.Yaw:F2}/{p.Pitch:F2}/{p.Roll:F2})";
        }

        public string Fmt(Vec3d v)
        {
            if (v == null)
                return "(null)";
            return $"(XYZ={v.X:F3}/{v.Y:F3}/{v.Z:F3})";
        }

        public override bool ContinueExecute(float dt)
        {
            entity.World.Logger.Debug($"[{entity.EntityId}] pos={Fmt(entity.ServerPos)} waypoint={Fmt(waypoint)}, target={Fmt(target)}");

            DebugParticles(target, 255, 255, 255);
            if (waypoint != null)
                DebugParticles(waypoint, 100, 255, 100);

            bool result = InternalContinueExecute(dt);
            previousPos = entity.ServerPos.Copy();

            entity.World.Logger.Debug($"  pos={Fmt(entity.ServerPos)} " +
                $"flyVector={Fmt(entity.Controls.FlyVector)}");

            return result;
        }

        void FlyTowards(Vec3d p, bool stopThere)
        {
            entity.Controls.IsFlying = true;
            entity.Controls.IsStepping = false;

            entity.Properties.Habitat = EnumHabitat.Air;

            Vec3d delta = p.Clone();
            delta.Sub(entity.ServerPos.XYZ);
            double distance = delta.Length();

            float targetRoll = 0;
            float targetYaw = (float)Math.Atan2(delta.X, delta.Z);
            if (targetYaw > entity.ServerPos.Yaw + Math.PI)
                targetYaw -= 2*(float)Math.PI;
            if (targetYaw < entity.ServerPos.Yaw - Math.PI)
                targetYaw += 2*(float)Math.PI;
            float targetPitch = (float)Math.Atan2(delta.Y, new Vec3d(delta.X, 0, delta.Z).Length());

            float turnLimit = 0.1F;
            float actualRoll = GameMath.Clamp(targetRoll, entity.ServerPos.Roll - turnLimit, entity.ServerPos.Roll + turnLimit);
            float actualYaw = GameMath.Clamp(targetYaw, entity.ServerPos.Yaw - turnLimit, entity.ServerPos.Yaw + turnLimit);
            float actualPitch = GameMath.Clamp(targetPitch, entity.ServerPos.Pitch - turnLimit, entity.ServerPos.Pitch + turnLimit);

            double speed = flightSpeed;
            if (Math.Abs(actualYaw - entity.ServerPos.Yaw) +
                Math.Abs(actualRoll - entity.ServerPos.Roll) +
                Math.Abs(actualPitch - entity.ServerPos.Pitch) > .03)
                speed /= 2;

            entity.ServerPos.Roll = actualRoll;
            entity.ServerPos.Yaw = actualYaw;
            entity.ServerPos.Pitch = actualPitch;

            entity.Controls.FlyVector.Set(0, 0, 0);
            if (stopThere)
                speed = Math.Min(speed, distance / 300);
            entity.Controls.FlyVector.Ahead(speed, entity.ServerPos.Pitch, entity.ServerPos.Yaw + Math.PI / 2);
        }

        void LandAt(Vec3d p)
        {
            float distance = (float)VecDistance(entity.ServerPos.XYZ, p);
            // Come in a little high, and then go down to land the last bit.
            if (distance > 1)
                p = new Vec3d(p.X, p.Y + 0.2, p.Z);
            FlyTowards(p, true);
        }

        bool InternalContinueExecute(float dt)
        {
            if (VecDistance(target, entity.ServerPos.XYZ) < 0.2)
            {
                // We've arrived!
                entity.Controls.FlyVector.Set(0, 0, 0);
                if (entity.ServerPos.BasicallySameAs(previousPos))
                    return false;
                else
                    return true;
            }

            if (VecDistance(startPos, entity.ServerPos.XYZ) > 2 &&
                    VecDistance(target, entity.ServerPos.XYZ) > 8) {
                FindBetterPerch();
            } else if (bestPerchScore <= 0 && VecDistance(target, entity.ServerPos.XYZ) < 8) {
                // Didn't find a perch in this direction. Try a different direction.
                target = entity.ServerPos.XYZ.AheadCopy(64, 0, entity.World.Rand.NextDouble() * Math.PI * 2);
            }

            if (AvoidCollision(Math.Min(3, (float)VecDistance(target, entity.ServerPos.XYZ))))
                return true;

            if (waypoint != null && VecDistance(waypoint, entity.ServerPos.XYZ) < 0.2)
                waypoint = null;
            if (waypoint != null)
            {
                FlyTowards(waypoint, false);
                return true;
            }

            LandAt(target);

            return true;
        }

        public override void FinishExecute(bool cancelled)
        {
            entity.World.Logger.Debug($"AiTaskPerch.FinishExecute(cancelled={cancelled})");
            entity.Controls.IsFlying = false;
            entity.Properties.Habitat = EnumHabitat.Land;
            base.FinishExecute(cancelled);
        }
    }
}