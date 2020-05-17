using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using System;
using Vintagestory.API;
using Vintagestory.API.Common.Entities;

[assembly: ModInfo( "Birds",
    Description = "TODO",
    Website     = "TODO",
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

        EntityPos previousPos;
        BlockPos bestPerch;
        float bestPerchScore;
        Vec3d startPos;

        Vintagestory.GameContent.EntityPartitioning partitionUtil;

        Vec3d target;
        Vec3d waypoint;

        public AiTaskPerch(EntityAgent entity) : base(entity)
        {
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
            entity.World.SpawnCubeParticles(p, p.ToVec3d(), 2, 30);

            float score = PerchScore(p);
            entity.World.Logger.Debug($"Found perch with score {score}: {p}");

            if (bestPerch == null || score > bestPerchScore)
            {
                bestPerch = p;
                bestPerchScore = score;
                entity.World.Logger.Debug($"New best perch with score {bestPerchScore}: {bestPerch}");
                target = new Vec3d(bestPerch.X + 0.5, bestPerch.Y + 0.5, bestPerch.Z + 0.5);
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

            entity.World.Logger.Debug($"target={target}");
        }

        static double VecDistance(Vec3d a, Vec3d b)
        {
            Vec3d delta = b.Clone();
            delta.Sub(a);
            return delta.Length();
        }

        bool AvoidCollision()
        {
            Block oneAhead = entity.World.BlockAccessor.GetBlock(entity.ServerPos.AheadCopy(1).AsBlockPos);
            Block twoAhead = entity.World.BlockAccessor.GetBlock(entity.ServerPos.AheadCopy(2).AsBlockPos);
            if (oneAhead.Id != 0 || twoAhead.Id != 0)
            {
                // find some open air and go there
                entity.World.SpawnCubeParticles(entity.ServerPos.AsBlockPos, entity.ServerPos.XYZ, 2, 20);
                (float score, float pitch, float yaw) bestSolution = (0, 0, 0);
                for (float yaw = 0; yaw < 2*Math.PI; yaw += (float) Math.PI/6)
                {
                    // Vary between 45 degrees down and up.
                    for (float pitch = -(float) Math.PI / 2; pitch <= Math.PI / 2; pitch += (float) Math.PI/6)
                    {
                        int distance;
                        for (distance = 1; distance < 4; distance++)
                        {
                            Block testBlock = entity.World.BlockAccessor.GetBlock(
                                entity.ServerPos.XYZ.AheadCopy(distance, pitch, yaw).AsBlockPos);
                            if (testBlock.Id != 0)
                                break;
                        }
                        float score = 10 * distance - Math.Abs(entity.ServerPos.Pitch - pitch) - Math.Abs(entity.ServerPos.Yaw - yaw);
                        if (score > bestSolution.score)
                            bestSolution = (score: score, pitch: pitch, yaw: yaw);
                    }
                }
                entity.World.Logger.Debug($"bestSolution={bestSolution}");
                if (bestSolution.score > 0)
                {
                    waypoint = entity.ServerPos.XYZ.AheadCopy(2, bestSolution.pitch, bestSolution.yaw);

                    if (oneAhead.Id == 0)
                    {
                        // Take drastic action to avoid imminent collision.
                        entity.Controls.FlyVector.Set(0, 0, 0);
                        entity.ServerPos.Pitch = bestSolution.pitch;
                        entity.ServerPos.Yaw = bestSolution.yaw;
                        entity.ServerPos.Roll = 0;
                        return true;
                    }
                }
            }

            return false;
        }

        public override bool ContinueExecute(float dt)
        {
            entity.World.Logger.Debug($"pos=[{entity.ServerPos}], waypoint=[{waypoint}], target=[{target}]");
            entity.World.SpawnCubeParticles(target.AsBlockPos, target, 2, 20);

            bool result = InternalContinueExecute(dt);
            previousPos = entity.ServerPos.Copy();

            entity.World.Logger.Debug($"roll={entity.ServerPos.Roll}, yaw={entity.ServerPos.Yaw}, pitch={entity.ServerPos.Pitch}, " +
                $"flyVector=[{entity.Controls.FlyVector}]");

            return result;
        }

        void FlyTowards(Vec3d p)
        {
            entity.Controls.IsFlying = true;

            Vec3d delta = p.Clone();
            delta.Sub(entity.ServerPos.XYZ);
            double distance = delta.Length();

            float targetRoll = 0;
            float targetYaw = (float)Math.Atan2(entity.Controls.FlyVector.X, entity.Controls.FlyVector.Z);
            float targetPitch = (float)Math.Atan(entity.Controls.FlyVector.Y);

            Vec3d targetFlyVector = new Vec3d(delta.X, delta.Y, delta.Z);
            if (distance > flightSpeed)
                targetFlyVector.Mul(flightSpeed / distance);

            float turnLimit = 0.1F;
            entity.ServerPos.Roll = GameMath.Clamp(targetRoll, entity.ServerPos.Roll - turnLimit, entity.ServerPos.Roll + turnLimit);
            entity.ServerPos.Yaw = GameMath.Clamp(targetYaw, entity.ServerPos.Yaw - turnLimit, entity.ServerPos.Yaw + turnLimit);
            entity.ServerPos.Pitch = GameMath.Clamp(targetPitch, entity.ServerPos.Pitch - turnLimit, entity.ServerPos.Pitch + turnLimit);

            double maxAcceleration = 0.01;
            Vec3d acceleration = new Vec3d(targetFlyVector.X, targetFlyVector.Y, targetFlyVector.Z);
            acceleration.Sub(entity.Controls.FlyVector);
            double accelerationMagnitude = acceleration.Length();
            if (accelerationMagnitude > maxAcceleration)
                acceleration.Mul(maxAcceleration / accelerationMagnitude);

            entity.Controls.FlyVector.Add(acceleration);
        }

        bool InternalContinueExecute(float dt)
        {
            if (VecDistance(target, entity.ServerPos.XYZ) < 0.2)
            {
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

            if (AvoidCollision())
                return true;

            if (waypoint != null && VecDistance(waypoint, entity.ServerPos.XYZ) < 0.2)
                waypoint = null;
            if (waypoint != null)
            {
                FlyTowards(waypoint);
                return true;
            }

            FlyTowards(target);

            return true;
        }

        public override void FinishExecute(bool cancelled)
        {
            entity.World.Logger.Debug($"AiTaskPerch.FinishExecute(cancelled={cancelled})");
            base.FinishExecute(cancelled);
        }
    }
}