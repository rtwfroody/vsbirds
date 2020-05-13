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

        Vintagestory.GameContent.EntityPartitioning partitionUtil;

        Vec3d target = new Vec3d();
        long endTime;

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
            return true;
        }

        public override void StartExecute()
        {
            entity.World.Logger.Debug("AiTaskPerch.StartExecute()");

            base.StartExecute();

            endTime = entity.World.ElapsedMilliseconds + minDuration + entity.World.Rand.Next(maxDuration - minDuration);

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
            int bestScore = -1;
            for (int x = (int) entity.ServerPos.X - searchRadius; x <= entity.ServerPos.X + searchRadius; x++)
            {
                for (int z = (int) entity.ServerPos.Z - searchRadius; z <= entity.ServerPos.Z + searchRadius; z++)
                {
                    if (square(x - (int) entity.ServerPos.X) + square(z - (int) entity.ServerPos.Z) < square(minDistance))
                        continue;

                    int smallestDelta = 1024;
                    int heightHere = entity.World.BlockAccessor.GetRainMapHeightAt(new BlockPos(x, 0, z));
                    for (int i = 0; i < offsets.GetLength(0); i++)
                    {
                        BlockPos pos = new BlockPos(x + offsets[i, 0], 0, z + offsets[i, 1]);
                        smallestDelta = Math.Min(smallestDelta,
                            Math.Abs(heightHere - entity.World.BlockAccessor.GetRainMapHeightAt(pos)));
                    }
                    if (smallestDelta > bestScore)
                    {
                        bestScore = smallestDelta;
                        target.Set(x, heightHere + 1, z);
                    }
                }
            }
            target.X += 0.5;
            target.Z += 0.5;
            entity.World.Logger.Debug($"target={target}");
        }

        public override bool ContinueExecute(float dt)
        {
            Vec3d delta = new Vec3d(target.X, target.Y, target.Z);
            delta.Sub(entity.ServerPos.XYZ);
            double distance = delta.Length();
            if (distance < 0.5)
            {
                entity.Controls.IsFlying = false;
                entity.Controls.Forward = false;
                entity.Controls.FlyVector.Set(0, 0, 0);
            }
            else
            {
                entity.Controls.IsFlying = true;

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

                entity.World.Logger.Debug($"pos=[{entity.ServerPos}], target=[{target}], " +
                        $"roll={entity.ServerPos.Roll}, yaw={entity.ServerPos.Yaw}, pitch={entity.ServerPos.Pitch}, " +
                        $"flyVector=[{entity.Controls.FlyVector}]");
            }

            return entity.World.ElapsedMilliseconds < endTime;
        }

        public override void FinishExecute(bool cancelled)
        {
            entity.World.Logger.Debug("AiTaskPerch.FinishExecute()");
            base.FinishExecute(cancelled);
        }
    }
}