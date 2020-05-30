using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using System;
using System.Collections.Generic;
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
            for (double yaw = 0; yaw < 2 * Math.PI; yaw += Math.PI/2)
            {
                for (double pitch = 0; pitch < 2 * Math.PI; pitch += Math.PI / 2)
                {
                    Vec3d p = new Vec3d(0, 0, 0);
                    p.Ahead(1, pitch, yaw);
                    api.World.Logger.Debug($"yaw={yaw:F2} pitch={pitch:F2} x={p.X:F2} y={p.Y:F2} z={p.Z:F2}");
                }
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
        }
    }

    // There's a difference between travel yaw/pitch and the display yaw/pitch.
    // Presumably this can be fixed by changing the model. I don't understand why there is a difference.
    // for yaw: display + PI/2 = travel
    // for pitch: display = travel
    public class FlightControl
    {
        const float arrivalDistance = 0.2f;

        // Configuration parameters
        double flightSpeed;

        ICachingBlockAccessor blockAccess;
        EntityAgent entity;
        Vec3d destination;
        EntityPos previousPos;
        Vec3d waypoint;
        List<Vec3d> waypointHistory;

        public enum Result
        {
            Complete,
            Incomplete,
            Unreachable
        }

        public void DebugParticles(Vec3d position, byte r, byte g, byte b, int quantity = 5)
        {
#if DEBUG
            SimpleParticleProperties particles = new SimpleParticleProperties(
                    quantity, quantity, ColorUtil.ColorFromRgba(b, g, r, 50),
                    position, new Vec3d(position.X, position.Y + 1, position.Z), new Vec3f(-1, -1, -1), new Vec3f(1, 1, 1));
            entity.World.SpawnParticles(particles);
#endif
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

        public string Fmt(Cuboidf cuboid)
        {
            return $"{cuboid.MinX:F2},{cuboid.MaxX:F2}/{cuboid.MinY:F2},{cuboid.MaxY:F2}/{cuboid.MinZ:F2},{cuboid.MaxZ:F2}";
        }

        public double VecDistance(Vec3d a, Vec3d b)
        {
            Vec3d delta = b.Clone();
            delta.Sub(a);
            return delta.Length();
        }

        /* Avoid collisions with blocks up to distance ahead. */
        // Return true if the collision can be avoided while making progress, false otherwise.
        private bool AvoidCollision(float distance)
        {
            const float collisionStep = 0.5f;
            float collisionDistance;
            Cuboidf cb = entity.CollisionBox;
            entity.World.Logger.Debug($"  collisionBox={cb.X1}/{cb.Y1}/{cb.Z1} -- {cb.X2}/{cb.Y2}/{cb.Z2}");
            for (collisionDistance = 0; collisionDistance < distance; collisionDistance += collisionStep)
            {
                Vec3d testPos = entity.ServerPos.XYZ.Ahead(collisionDistance, entity.ServerPos.Pitch, entity.ServerPos.Yaw + Math.PI / 2);
                //entity.World.Logger.Debug($"    testPos={Fmt(testPos)}");
                DebugParticles(testPos, 100, 100, 100, quantity: 1);
                if (entity.World.CollisionTester.IsColliding(blockAccess, entity.CollisionBox,
                        testPos))
                    break;
            }

            entity.World.Logger.Debug($"  collisionDistance={collisionDistance} distance={distance}");

            if (collisionDistance < distance)
            {
                // find some open air and go there
                DebugParticles(entity.ServerPos.XYZ, 255, 100, 100);
                const float minScore = -1000;
                (float score, float distance, float yaw, float pitch) bestSolution = (minScore, 0, 0, 0);
                for (float yaw = 0; yaw < 2 * Math.PI; yaw += (float)Math.PI / 4)
                {
                    // Vary between 45 degrees down and up.
                    for (float pitch = -(float)Math.PI / 2; pitch <= Math.PI / 2; pitch += (float)Math.PI / 4)
                    {
                        float d;
                        // Don't start at 0, because we might be just barely in a collision already.
                        for (d = collisionStep; d < 4; d += collisionStep)
                        {
                            Vec3d pos = entity.ServerPos.XYZ.AheadCopy(d, pitch, yaw);
                            if (entity.World.CollisionTester.IsColliding(blockAccess, entity.CollisionBox, pos))
                                break;
                            float distanceToTarget = (float)VecDistance(destination, pos);
                            // Don't pick a waypoint too close to the target, or else we won't have
                            // the turn radius to make it to the target once we get there.
                            if (distanceToTarget > 2)
                            {
                                float score = -(float)distanceToTarget + d;
                                //entity.World.Logger.Debug($"    score={score:F2} yaw={yaw:F2} pitch={pitch:F2} d={d:F2} distanceToTarget={distanceToTarget:F2} pos={Fmt(pos)}");
                                if (score > bestSolution.score)
                                    bestSolution = (score: score, distance: d, yaw: yaw, pitch: pitch);
                            }
                        }
                    }
                }
                entity.World.Logger.Debug($"  bestSolution={bestSolution}");
                if (bestSolution.score > minScore)
                {
                    waypoint = entity.ServerPos.XYZ.AheadCopy(bestSolution.distance, bestSolution.pitch, bestSolution.yaw);
                    foreach (Vec3d w in waypointHistory)
                    {
                        // If we're setting a waypoint that's really close to one
                        // we already set, we're probably stuck in some kind of loop,
                        // and we should try to go somewhere else (or use a different algorithm).
                        if (VecDistance(w, waypoint) < 0.2)
                            return false;
                    }
                    waypointHistory.Add(waypoint);
                    entity.World.Logger.Debug($"  waypoint={Fmt(waypoint)}");

                    if (collisionDistance < 1.5)
                    {
                        // Take drastic action to avoid imminent collision.
                        entity.ServerPos.Pitch = bestSolution.pitch;
                        entity.ServerPos.Yaw = bestSolution.yaw - (float)Math.PI / 2;
                        entity.ServerPos.Roll = 0;
                        entity.Controls.FlyVector.Set(0, 0, 0);
                        entity.Controls.FlyVector.Ahead(flightSpeed / 2, bestSolution.pitch, bestSolution.yaw);
                        if (collisionDistance <= 0)
                        {
                            // We might be completely stuck. Warp a little.
                            entity.ServerPos.Y += .1;
                        }
                    }
                }
                else
                {
                    // No solution found. We're probably stuck inside a block.
                    /*
                     // Die a little.
                     entity.ReceiveDamage(
                        new DamageSource()
                        {
                            Source = EnumDamageSource.Block,
                            Type = EnumDamageType.Crushing
                        }, 1);*/
                    DebugParticles(entity.ServerPos.XYZ, 255, 0, 0);
                }
            }

            return true;
        }

        void updateDestination(Vec3d p)
        {
            entity.World.Logger.Debug($"  updateDestination({Fmt(p)})");
            destination = p;
            waypoint = null;
            waypointHistory = new List<Vec3d>();
        }

        public FlightControl(EntityAgent entity)
        {
            this.entity = entity;
            blockAccess = ((ICoreServerAPI)entity.Api).WorldManager.GetCachingBlockAccessor(true, true);
        }

        public void LoadConfig(JsonObject taskConfig, JsonObject aiConfig)
        {
            flightSpeed = taskConfig["flightSpeed"].AsDouble(.03);
        }

        // Return true if we have further to go, false otherwise.
        Result FlyTowards(Vec3d p, bool stopThere)
        {
            if (destination == null || VecDistance(p, destination) > .01)
                updateDestination(p);

            if (waypoint != null)
            {
                if (VecDistance(entity.ServerPos.XYZ, waypoint) < 0.2)
                {
                    waypoint = null;
                }
                else
                {
                    p = waypoint;
                    stopThere = false;
                    DebugParticles(waypoint, 50, 255, 50, 1);
                }
            }

            entity.Controls.IsFlying = true;
            entity.Controls.IsStepping = false;

            entity.Properties.Habitat = EnumHabitat.Air;

            Vec3d delta = p.Clone();
            delta.Sub(entity.ServerPos.XYZ);
            float distance = (float)delta.Length();
            if (distance < arrivalDistance)
                return Result.Complete;

            float targetRoll = 0;
            float targetYaw = (float)Math.Atan2(delta.X, delta.Z);
            if (targetYaw > entity.ServerPos.Yaw + Math.PI)
                targetYaw -= 2 * (float)Math.PI;
            if (targetYaw < entity.ServerPos.Yaw - Math.PI)
                targetYaw += 2 * (float)Math.PI;
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

            // All set up for what we want to do. But if that's going to lead to a collision, that's more important.
            if (waypoint == null && !AvoidCollision(Math.Min(3, distance)))
                return Result.Unreachable;

            return Result.Incomplete;
        }

        // Return true if we have further to go, false otherwise.
        public Result LandAt(Vec3d p)
        {
            float distance = (float)VecDistance(entity.ServerPos.XYZ, p);
            // Come in a little high, and then go down to land the last bit.
            if (distance > 1)
                p = new Vec3d(p.X, p.Y + 0.2, p.Z);
            else if (distance < arrivalDistance)
            {
                // We've arrived!
                entity.Controls.FlyVector.Set(0, 0, 0);
                if (previousPos != null && entity.ServerPos.BasicallySameAs(previousPos))
                {
                    previousPos = null;
                    return Result.Complete;
                }
                else
                {
                    previousPos = entity.ServerPos.Copy();
                    return Result.Incomplete;
                }
            }

            return FlyTowards(p, true);
        }

    }

    public class AiTaskPerch : Vintagestory.API.Common.AiTaskBase
    {
        // Configuration parameters.
        int searchRadius;
        int minDistance;
        int minDuration, maxDuration;

        ICachingBlockAccessor blockAccess;
        BlockPos bestPerch;
        float bestPerchScore;
        Vec3d startPos;
        FlightControl fc;

        Vec3d target;

        public AiTaskPerch(EntityAgent entity) : base(entity)
        {
            blockAccess = ((ICoreServerAPI)entity.Api).WorldManager.GetCachingBlockAccessor(true, true);
            fc = new FlightControl(entity);
        }

        public override void LoadConfig(JsonObject taskConfig, JsonObject aiConfig)
        {
            base.LoadConfig(taskConfig, aiConfig);
            fc.LoadConfig(taskConfig, aiConfig);
            searchRadius = taskConfig["searchRadius"].AsInt(32);
            minDistance = taskConfig["minDistance"].AsInt(12);
            minDuration = taskConfig["minDuration"].AsInt(5000);
            maxDuration = taskConfig["maxDuration"].AsInt(60000);
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
            double distance = 8 + entity.World.Rand.NextDouble() * (searchRadius - 8);
            double yaw = entity.ServerPos.Yaw + Math.PI / 3 + entity.World.Rand.NextDouble() * Math.PI / 3;
            Vec3d spot = entity.ServerPos.XYZ.AheadCopy(distance, 0, yaw);
            BlockPos p = new BlockPos((int)spot.X, (int)spot.Y, (int)spot.Z);
            p.Y = entity.World.BlockAccessor.GetRainMapHeightAt(p);

            float score = PerchScore(p);

            if (bestPerch == null || score > bestPerchScore)
            {
                bestPerch = p;
                bestPerchScore = score;
                Block b = entity.World.BlockAccessor.GetBlock(p);
                float maxHeight = 0;
                if (b.CollisionBoxes != null) {
                    foreach (Cuboidf cuboid in b.CollisionBoxes)
                    {
                        maxHeight = Math.Max(maxHeight, cuboid.Height);
                    }
                }
                entity.World.Logger.Debug($"New best perch with score {bestPerchScore}: {bestPerch}");
                target = new Vec3d(bestPerch.X + 0.5, bestPerch.Y + maxHeight, bestPerch.Z + 0.5);
            }
        }

        public override void StartExecute()
        {
            entity.World.Logger.Debug("AiTaskPerch.StartExecute()");

            base.StartExecute();

            StartOver();

#if DEBUG
            // Look for quartz, which I'm using to make the crow
            // fly routes that I need for testing.
            for (int x = -searchRadius; x <= searchRadius; x++)
            {
                for (int z = -searchRadius; z <= searchRadius; z++)
                {
                    if (Math.Abs(x) + Math.Abs(z) < 8)
                        continue;
                    BlockPos q = new BlockPos((int)entity.ServerPos.X + x, 0, (int)entity.ServerPos.Z + z);
                    q.Y = entity.World.BlockAccessor.GetRainMapHeightAt(q);
                    Block b = entity.World.BlockAccessor.GetBlock(q);
                    if (b.Id == 2413) // crystal rose quartz large
                    {
                        q.Y += 1;
                        bestPerch = q;
                        target = new Vec3d(bestPerch.X + 0.5, bestPerch.Y, bestPerch.Z + 0.5);
                        bestPerchScore = 10000;
                        break;
                    }
                }
            }
#endif

            entity.World.Logger.Debug($"target={fc.Fmt(target)}");
        }

        void StartOver()
        {
            startPos = entity.ServerPos.XYZ;
            target = entity.ServerPos.XYZ.AheadCopy(64, 0, entity.World.Rand.NextDouble() * Math.PI * 2);
            bestPerch = null;
        }

        // To make the bird look right, we need to adjust yaw by 90 degrees.
        // But then Ahead() is (obviously) off by 90 degrees as well, so we have to compensate for that.

        public override bool ContinueExecute(float dt)
        {
            entity.World.Logger.Debug($"[{entity.EntityId}] pos={fc.Fmt(entity.ServerPos)} target={fc.Fmt(target)}");

            fc.DebugParticles(target, 255, 255, 255);

            bool result = InternalContinueExecute(dt);

            entity.World.Logger.Debug($"  pos={fc.Fmt(entity.ServerPos)} " +
                $"flyVector={fc.Fmt(entity.Controls.FlyVector)}");

            return result;
        }

        bool InternalContinueExecute(float dt)
        {
            if (fc.VecDistance(startPos, entity.ServerPos.XYZ) > 2 &&
                    fc.VecDistance(target, entity.ServerPos.XYZ) > 8) {
                FindBetterPerch();
            } else if (bestPerchScore <= 0 && fc.VecDistance(target, entity.ServerPos.XYZ) < 8) {
                // Didn't find a perch in this direction. Try a different direction.
                StartOver();
            }

            switch (fc.LandAt(target))
            {
                case FlightControl.Result.Complete:
                    return false;
                case FlightControl.Result.Incomplete:
                    return true;
                case FlightControl.Result.Unreachable:
                    // Go somewhere else.
                    StartOver();
                    return true;
            }
            // Should never get here.
            return false;
        }

        public override void FinishExecute(bool cancelled)
        {
            entity.World.Logger.Debug($"AiTaskPerch.FinishExecute(cancelled={cancelled})");
            // It would be nice to let regular game gravity work while we're perched, but
            // then if we try to perch on leaves in a tree, we drop through them to the ground.
            // If the tree is tall, this kills the bird.
            entity.ServerPos.Pitch = 0;
            entity.ServerPos.Roll = 0;
            //entity.Controls.IsFlying = false;
            //entity.Properties.Habitat = EnumHabitat.Land;
            base.FinishExecute(cancelled);
        }
    }
}