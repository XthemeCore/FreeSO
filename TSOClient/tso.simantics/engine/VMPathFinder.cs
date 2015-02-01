﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TSO.Simantics.model;
using Microsoft.Xna.Framework;
using tso.world.model;
using TSO.Files.formats.iff.chunks;
using tso.world.components;
using TSO.Vitaboy;
using TSO.Simantics.utils;

namespace TSO.Simantics.engine
{
    /// <summary>
    /// Determines the path to a set destination from an avatar and provides a system to walk them there. First finds the
    /// set of room portals to get to the target's room (calls their portal functions in order), then pathfinds to the relevant 
    /// position once in the same room.
    /// 
    /// If in the same room or a flag is set to "ignore rooms", the room step is ignored and the character is directly routed 
    /// to the specified position. Pathfinders are normally pushed to the stack, but behave completely differently from
    /// stack frames. You can imagine an example case, where the avatar is currently routing to a door, but the final destination
    /// is the piano.
    /// 
    /// STACK:
    /// Private: Interaction - Play (piano)
    /// Private: route to piano and sit (piano)
    /// VMPathFinder: Go on top of, opposing direction. (this is passed as a position and direction.)
    /// Semi-global: Portal function (door)
    /// Semi-global: Goto door and gace through (door)
    /// VMPathFinder: Go on top of, same direction. (passed as position direction, but this time destination is in same room, so we call no portal functions)
    /// 
    /// Once the first portal function returns true, we will pop back to the initial VMPathFinder to route to the second or the object.
    /// This can repeat many times. 
    /// 
    /// However, if a portal function returns false, we will need to re-evaluate the portal route to the
    /// destination, discounting the route from the previous portal to the portal we failed to route to. (nodes are no longer connected) 
    /// The Sims 1 does not do this, but routing problems annoy me as much as everyone else. :)
    /// </summary>
    public class VMPathFinder : VMStackFrame
    {
        public Stack<VMRoomPortal> Rooms;
        public Stack<Point> WalkTo;
        private double WalkDirection;
        private bool Walking = false;

        private bool Turning = false;
        private bool AttemptedChair = false;
        private float TurnTweak = 0;

        private Vector3 CurrentWaypoint;

        public List<VMFindLocationResult> Choices;
        public VMFindLocationResult CurRoute;

        public bool InitRoutes(List<VMFindLocationResult> choices) //returns false if we can't find a single route
        {
            Choices = choices; //should be ordered by most preferred first, with a little bit of random shuffling to keep things interesting for "wander"
            //style movements. Also includes flags dictating if this route goes through walls etc.
            bool found = false;
            while (!found && Choices.Count > 0) {
                found = AttemptRoute(Choices[0]);
                Choices.RemoveAt(0);
            }
            return found;
        }

        private bool AttemptRoute(VMFindLocationResult route) { //returns false if there is no room portal route to the destination room.
            CurRoute = route;
            var avatar = (VMAvatar)Caller;

            //if we are routing to a chair, let it take over.
            if (route.Chair != null)
            {
                AttemptedChair = false;
                return true;
            }

            Rooms = new Stack<VMRoomPortal>();

            var DestRoom = VM.Context.GetRoomAt(route.Position);
            var MyRoom = VM.Context.GetRoomAt(avatar.Position);

            if (DestRoom == MyRoom || (route.Flags & SLOTFlags.IgnoreRooms) > 0) return true; //we don't have to do any room finding for this
            else {
                //find shortest room traversal to destination. Simple A* pathfind.
                //Portals are considered nodes to allow multiple portals between rooms to be considered.

                var openSet = new List<VMRoomPortal>(); //we use this like a queue, but we need certain functions for sorted queue that are only provided by list.
                var closedSet = new HashSet<VMRoomPortal>();

                var gScore = new Dictionary<VMRoomPortal, double>();
                var fScore = new Dictionary<VMRoomPortal, double>();
                var parents = new Dictionary<VMRoomPortal, VMRoomPortal>();

                var StartPortal = new VMRoomPortal(Caller.ObjectID, MyRoom); //consider the sim as a portal to this room (as a starting point)
                openSet.Add(StartPortal);
                gScore[StartPortal] = 0;
                fScore[StartPortal] = GetDist(Caller.Position, route.Position);

                while (openSet.Count != 0) {
                    var current = openSet[0];
                    openSet.RemoveAt(0);

                    if (current.TargetRoom == DestRoom) {
                        //this portal gets us to the room.
                        while (current != StartPortal) //push previous portals till we get to our first "portal", the sim in its current room (we have already "traversed" this portal)
                        {
                            Rooms.Push(current);
                            current = parents[current];
                        }
                        return true;
                    }

                    closedSet.Add(current);

                    var portals = VM.Context.RoomInfo[current.TargetRoom].Portals;

                    foreach (var portal in portals) { //evaluate all neighbor portals
                        if (closedSet.Contains(portal)) continue; //already evaluated!

                        var pos = VM.GetObjectById(portal.ObjectID).Position;
                        var gFromCurrent = gScore[current] + GetDist(VM.GetObjectById(current.ObjectID).Position, pos);
                        var newcomer = !openSet.Contains(portal);

                        if (newcomer || gFromCurrent < gScore[portal]) { 
                            parents[portal] = current; //best parent for now
                            gScore[portal] = gFromCurrent;
                            fScore[portal] = gFromCurrent + GetDist(pos, route.Position);
                            if (newcomer) { //add and move to relevant position
                                OpenSetSortedInsert(openSet, fScore, portal);
                            } else { //remove and reinsert to refresh sort
                                openSet.Remove(portal);
                                OpenSetSortedInsert(openSet, fScore, portal);
                            }
                        }
                    }
                }

                return false;
            }
        }

        private bool AttemptWalk() //pathfinds to the destination position from the current. The room pathfind should get us to the same room before we do this.
        {
            WalkTo = new Stack<Point>();
            WalkTo.Push(new Point((int)CurRoute.Position.X, (int)CurRoute.Position.Y));
            return true;
        }

        private void OpenSetSortedInsert(List<VMRoomPortal> set, Dictionary<VMRoomPortal, double> fScore, VMRoomPortal portal)
        {
            var myScore = fScore[portal];
            for (int i = 0; i < set.Count; i++)
            {
                if (myScore < fScore[set[i]])
                {
                    set.Insert(i, portal);
                    return;
                }
            }
            set.Add(portal);
        }

        private double GetDist(Vector3 pos1, Vector3 pos2)
        {
            return Math.Sqrt(Math.Pow(pos1.X - pos2.X, 2) + Math.Pow(pos1.Y - pos2.Y, 2)) + Math.Abs(pos1.Z-pos2.Z)*10; //floors add a distance of 30.
        }

        private bool PushEntryPoint(int entryPoint, VMEntity ent) {
            if (ent.EntryPoints[entryPoint].ActionFunction != 0)
            {
                bool Execute;
                if (ent.EntryPoints[entryPoint].ConditionFunction != 0) //check if we can definitely execute this...
                {
                    var Behavior = ent.GetBHAVWithOwner(ent.EntryPoints[entryPoint].ConditionFunction, VM.Context);
                    Execute = (VMThread.EvaluateCheck(VM.Context, Caller, new VMQueuedAction()
                    {
                        Callee = ent,
                        CodeOwner = Behavior.owner,
                        StackObject = ent,
                        Routine = VM.Assemble(Behavior.bhav),
                    }) == VMPrimitiveExitCode.RETURN_TRUE);

                }
                else
                {
                    Execute = true;
                }

                if (Execute)
                {
                    //push it onto our stack, except now the object owns our soul! when we are returned to we can evaluate the result and determine if the action failed.
                    var Behavior = ent.GetBHAVWithOwner(ent.EntryPoints[entryPoint].ActionFunction, VM.Context);
                    var routine = VM.Assemble(Behavior.bhav);
                    var childFrame = new VMStackFrame
                    {
                        Routine = routine,
                        Caller = Caller,
                        Callee = ent,
                        CodeOwner = Behavior.owner,
                        StackObject = ent
                    };
                    childFrame.Args = new short[routine.Arguments];
                    Thread.Push(childFrame);
                    return true;
                }
                else
                {
                    return false; //could not execute portal function. todo: re-evaluate room route
                }
            }
            else
            {
                return false;
            }
        }

        public VMPrimitiveExitCode Tick()
        {
            if (CurRoute.Chair != null) {
                if (!AttemptedChair)
                {
                    if (PushEntryPoint(26, CurRoute.Chair)) return VMPrimitiveExitCode.CONTINUE;
                    else return VMPrimitiveExitCode.RETURN_FALSE;
                } else 
                    return VMPrimitiveExitCode.RETURN_TRUE;
            }
            //If we are sitting, and the target is not this seat we need to call the stand function on the object we are contained within.

            if (((VMAvatar)Caller).GetPersonData(VMPersonDataVariable.Posture) == 1)
            {
                //push it onto our stack, except now the portal owns our soul! when we are returned to we can evaluate the result and determine if the route failed.
                var chair = VM.GetObjectById(Caller.GetValue(VMStackObjectVariable.ContainerId));

                if (chair == null) return VMPrimitiveExitCode.RETURN_FALSE; //we're sitting, but are not bound to a chair. We should probably just set posture to 0 in this case.

                if (PushEntryPoint(27, chair)) return VMPrimitiveExitCode.CONTINUE; //27 is stand. TODO: set up an enum for these
                else return VMPrimitiveExitCode.RETURN_FALSE;
            }

            if (Rooms.Count > 0)
            { //push portal function of next portal
                var portal = Rooms.Pop();
                var ent = VM.GetObjectById(portal.ObjectID);
                if (PushEntryPoint(15, ent)) return VMPrimitiveExitCode.CONTINUE; //15 is portal function
                else return VMPrimitiveExitCode.GOTO_FALSE; //could not execute portal function
            }
            else
            { //direct routing to a position - all required portals have been reached.
                var avatar = (VMAvatar)Caller;
                if (Turning)
                {
                    if (avatar.CurrentAnimationState.EndReached) {
                        Turning = false;
                        ((AvatarComponent)avatar.WorldUI).RadianDirection = WalkDirection;
                        StartWalkAnimation();
                    }
                    else
                    {
                        ((AvatarComponent)avatar.WorldUI).RadianDirection += TurnTweak; //while we're turning, adjust our direction
                    }
                    return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                }
                else
                {
                    if (WalkTo == null)
                    {
                        AttemptWalk();
                        return VMPrimitiveExitCode.CONTINUE;
                    }
                    else
                    {
                        if (!Walking)
                        {
                            var remains = AdvanceWaypoint();
                            if (!remains) return VMPrimitiveExitCode.RETURN_TRUE; //we are here!

                            BeginWalk();
                        }
                        else
                        {
                            if (Vector3.Distance(Caller.Position, CurrentWaypoint) < 0.10f)
                            {
                                var remains = AdvanceWaypoint();
                                if (!remains)
                                {
                                    avatar.Direction = (Direction)((int)CurRoute.Flags & 255);
                                    avatar.SetPersonData(VMPersonDataVariable.RouteEntryFlags, (short)CurRoute.RouteEntryFlags);
                                    return VMPrimitiveExitCode.RETURN_TRUE; //we are here!
                                }
                            }

                            if (avatar.CurrentAnimationState.EndReached) StartWalkAnimation();
                            //normal sims can move 0.05 units in a frame.
                            ((AvatarComponent)avatar.WorldUI).RadianDirection = WalkDirection;
                            Caller.Position += new Vector3(-(float)Math.Sin(WalkDirection) * 0.05f, (float)Math.Cos(WalkDirection) * 0.05f, 0);
                        }
                        return VMPrimitiveExitCode.CONTINUE_NEXT_TICK;
                    }
                }
            }
            return VMPrimitiveExitCode.RETURN_FALSE;
        }

        private double DirectionDifference(double dir1, double dir2)
        {
            double directionDiff = dir2 - dir1;
            while (directionDiff > Math.PI) directionDiff -= 2 * Math.PI;
            while (directionDiff < -Math.PI) directionDiff += 2 * Math.PI;

            return directionDiff;
        }

        private void BeginWalk()
        { //faces the avatar towards the initial walk direction and begins walking.
            var obj = (VMAvatar)Caller;
            var avatar = (AvatarComponent)Caller.WorldUI;

            var directionDiff = DirectionDifference(avatar.RadianDirection, WalkDirection);

            int off = (directionDiff > 0) ? 0 : 1;
            var absDiff = Math.Abs(directionDiff);

            string animName;
            if (absDiff >= Math.PI - 0.01) //full 180 turn
            {
                animName = obj.WalkAnimations[4 + off];
                TurnTweak = 0;
            }
            else if (absDiff >= (Math.PI/2) - 0.01) //>=90 degree turn
            {
                animName = obj.WalkAnimations[6 + off];
                TurnTweak = (float)(absDiff - (Math.PI / 2));
            }
            else if (absDiff >= (Math.PI / 4) - 0.01) //>=45 degree turn
            {
                animName = obj.WalkAnimations[6 + off];
                TurnTweak = (float)(absDiff - (Math.PI / 4));
            }
            else
            {
                //turning animation not needed for small rotations!
                StartWalkAnimation();
                return;
            }
            var anim = PlayAnim(animName, obj);
            TurnTweak /= anim.NumFrames;
            if (off == 1) TurnTweak = -TurnTweak;
            Turning = true;
            //return
        }

        private void StartWalkAnimation()
        {
            var obj = (VMAvatar)Caller;
            var anim = PlayAnim(obj.WalkAnimations[20], obj); //TODO: maybe an enum for this too. Maybe just an enum for everything.
            Walking = true;
        }

        private Animation PlayAnim(string name, VMAvatar avatar)
        {
            var animation = TSO.Content.Content.Get().AvatarAnimations.Get(name + ".anim");

            avatar.CurrentAnimation = animation;
            avatar.CurrentAnimationState = new VMAnimationState();
            avatar.Avatar.LeftHandGesture = SimHandGesture.Idle;
            avatar.Avatar.RightHandGesture = SimHandGesture.Idle;

            foreach (var motion in animation.Motions)
            {
                if (motion.TimeProperties == null) { continue; }

                foreach (var tp in motion.TimeProperties)
                {
                    foreach (var item in tp.Items)
                    {
                        avatar.CurrentAnimationState.TimePropertyLists.Add(item);
                    }
                }
            }

            /** Sort time property lists by time **/
            avatar.CurrentAnimationState.TimePropertyLists.Sort(new TimePropertyListItemSorter());
            return animation;
        }

        private bool AdvanceWaypoint()
        {
            if (WalkTo.Count == 0) return false;
            var point = WalkTo.Pop();
            if (WalkTo.Count > 1)
            {
                CurrentWaypoint = new Vector3(point.X + 0.5f, point.Y + 0.5f, Caller.Position.Z);
            }
            else CurrentWaypoint = CurRoute.Position; //go directly to position at last

            WalkDirection = Math.Atan2(Caller.Position.X - CurrentWaypoint.X, CurrentWaypoint.Y - Caller.Position.Y); //y+ as north. x+ is -90 degrees.
            return true;
        }
    }
}
