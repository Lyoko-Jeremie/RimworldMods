using System;
using FullyAutomaticOmniCrafter;
using RimWorld;
using Verse;
using Verse.AI;
using HarmonyLib;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string message) { checks++;if(!value)throw new Exception(message); }
    private static IntVec3 Cell(int x,int z)=>new IntVec3(x,0,z);
    private static Thing Target(Map map,int x,int z)=>new Thing {MapHeld=map,Position=Cell(x,z)};
    private static void Main()
    {
        Map map=new Map();Pawn pawn=new Pawn {MapHeld=map,Position=Cell(1,1),thingIDNumber=1};
        Thing target=Target(map,15,15);target.width=3;target.height=2;
        foreach(PathEndMode mode in new[]{PathEndMode.OnCell,PathEndMode.Touch,PathEndMode.ClosestTouch})
        {
            Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,mode,out IntVec3 end),"目标应可达");
            Check(OmniWorkProxyNavigation.IsAt(pawn,map,end,target,mode),"选点必须满足同一到达规则");
            Check(!OmniWorkProxyNavigation.IsAt(pawn,map,pawn.Position,target,mode),"远处不能视为到达");
            using(PawnPath path=OmniWorkProxyNavigation.FindPath(pawn,map,pawn.Position,target,mode))
            {
                Check(path.Found&&path.Peek(0)==pawn.Position,"路径起点");
                Check(path.Peek(path.NodesLeftCount-1)==end,"路径终点");
                for(int i=1;i<path.NodesLeftCount;i++)
                    Check((path.Peek(i)-path.Peek(i-1)).LengthHorizontalSquared<=2,"兼容路径必须由相邻格构成");
            }
        }
        target.def.hasInteractionCell=true;target.InteractionCell=Cell(14,16);
        Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,PathEndMode.InteractionCell,out IntVec3 interaction)&&interaction==target.InteractionCell,"精确交互格");
        target.InteractionCell=Cell(-1,16);
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,PathEndMode.InteractionCell,out _),"越界交互格不能通过裁剪变为合法格");
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,Cell(-1,2),PathEndMode.Touch,out _),"越界目标");
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,Cell(-1,2),target,PathEndMode.Touch,out _),"越界起点");
        target.MapHeld=new Map();
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,PathEndMode.Touch,out _),"跨地图目标");
        target.MapHeld=map;target.Destroyed=true;
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,PathEndMode.Touch,out _),"已销毁目标");target.Destroyed=false;
        Thing held=Target(map,0,0);held.Spawned=false;held.Parent=target;
        Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,held,PathEndMode.Touch,out IntVec3 heldEnd)&&heldEnd==Cell(14,14),"持有物以父对象为访问位置");
        Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,target.Position,target,PathEndMode.Touch,out IntVec3 adjacent,true,true)&&!target.OccupiedRect().Contains(adjacent),"移出蓝图");
        map.reservationManager.reserved.Add(adjacent);
        Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,target.Position,target,PathEndMode.Touch,out IntVec3 alternative,true,true)&&alternative!=adjacent,"预约冲突换落点");
        Thing edge=Target(map,0,0);
        Check(OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,edge,PathEndMode.Touch,out IntVec3 edgeEnd)&&edgeEnd.InBounds(map),"地图边缘落点");
        object grid=OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid;OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid=null;
        Check(!OmniWorkProxyNavigation.TryFindEnd(pawn,map,pawn.Position,target,PathEndMode.Touch,out _),"网格缺失必须失败");OmniWorkstationDefOf.FAOC_OmniWorkProxyPathGrid=grid;
        TestFailures(map,pawn);
        TestMovementPatches();
        Console.WriteLine($"PASS: {checks} navigation / retry assertions");
    }
    private static void TestMovementPatches()
    {
        var harmony=new Harmony("tests.omni.navigation");
        harmony.CreateClassProcessor(typeof(Patch_OmniNavigation_Start)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniNavigation_Tick)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniNavigation_Reset)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniNavigation_Immediate)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniNavigation_GotoBuild)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniWorkFailure_ThingCandidate)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniWorkFailure_CellCandidate)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OmniWorkFailure_End)).Patch();
        Map map=new Map();Pawn pawn=new Pawn {MapHeld=map,Position=Cell(1,1),CurJob=new Job()};
        int steps=0;
        pawn.jobs.onArrival=()=> { steps++;if(steps==1)pawn.pather.StartPath(Cell(20,20),PathEndMode.OnCell); };
        pawn.pather.StartPath(Cell(10,10),PathEndMode.OnCell);
        Check(pawn.Position==Cell(1,1)&&pawn.pather.Moving&&steps==0,"StartPath 必须延后完成");
        pawn.pather.ResetToCurrentPosition();
        pawn.pather.PatherTick();
        Check(pawn.Position==Cell(10,10)&&steps==1&&pawn.pather.Moving,"到达回调启动下一段不会被清掉");
        pawn.pather.PatherTick();
        Check(pawn.Position==Cell(20,20)&&steps==2&&!pawn.pather.Moving,"连续搬运第二段完成");
        Check(ReachabilityImmediate.CanReachImmediate(pawn.Position,Cell(20,20),map,PathEndMode.OnCell,pawn),"原版即时到达入口接入专用规则");
        pawn.jobs.onArrival=null;
        Thing doomed=Target(map,25,25);pawn.CurJob=new Job {targetA=doomed};
        pawn.pather.StartPath(doomed,PathEndMode.Touch);doomed.Destroyed=true;pawn.pather.PatherTick();
        Check(pawn.jobs.failures==1&&!pawn.pather.Moving,"等待期间目标销毁必须正常失败");
        Pawn normal=new Pawn {MapHeld=map,Proxy=false};
        Check(!ReachabilityImmediate.CanReachImmediate(Cell(1,1),Cell(1,1),map,PathEndMode.OnCell,normal),"普通居民保留原方法");
        Thing blueprint=Target(map,10,10);pawn.CurJob=new Job {targetB=blueprint};
        Toil build=Toils_Goto.GotoBuild(TargetIndex.B);build.actor=pawn;build.initAction();
        Check(map.reservationManager.reserved.Count==1,"建造落点先预约");pawn.pather.PatherTick();
        Check(!blueprint.OccupiedRect().Contains(pawn.Position),"交付落点在蓝图外");
        WorkGiver_Scanner scanner=new TestScanner();
        Thing bad=Target(map,3,3),good=Target(map,4,4);
        Job badJob=new Job {targetA=bad,workGiverDef=scanner.def};
        map.manager.WorkFailures.Ended(pawn,badJob,JobCondition.Errored);
        Thing selected=null;
        foreach(Thing candidate in new[]{bad,good})
            if(scanner.HasJobOnThing(pawn,candidate)) { selected=candidate;break; }
        Check(selected==good,"派生扫描器必须跳过失败候选并继续选择正常工作");
        Check(scanner.HasJobOnThing(normal,bad),"失败隔离不能影响普通居民");
        harmony.UnpatchAll(harmony.Id);
    }
    private static void TestFailures(Map map,Pawn pawn)
    {
        var cache=map.manager.WorkFailures;var giver=new WorkGiverDef();
        Thing blueprint=Target(map,20,20),other=Target(map,22,20),resource=Target(map,5,5);
        Pawn second=new Pawn {MapHeld=map,Position=Cell(1,2),thingIDNumber=2};
        Job job=new Job {def=JobDefOf.HaulToContainer,workGiverDef=giver,targetA=resource,targetB=blueprint,targetC=blueprint};pawn.CurJob=job;
        cache.Started(pawn,job);cache.Ended(pawn,job,JobCondition.ErroredPather);
        Check(!cache.Allows(second,giver,blueprint),"失败在代理间共享");
        Check(cache.Allows(second,giver,other),"其他任务不受影响");
        Check(cache.Allows(second,new WorkGiverDef(),blueprint),"其他工作来源不受影响");
        GenTicks.TicksGame=120;
        Check(cache.Allows(second,giver,blueprint),"冷却到期可试探");
        cache.Started(second,job);
        Check(!cache.Allows(pawn,giver,blueprint)&&cache.Allows(second,giver,blueprint),"试探独占");
        cache.Ended(second,job,JobCondition.Incompletable);
        GenTicks.TicksGame=240;Check(!cache.Allows(pawn,giver,blueprint),"重复失败延长退避");
        GenTicks.TicksGame=360;Check(cache.Allows(pawn,giver,blueprint),"第二次退避到期");
        cache.Started(pawn,job);cache.Ended(pawn,job,JobCondition.Succeeded);
        Check(cache.Allows(second,giver,blueprint),"成功解除隔离");
        cache.Started(pawn,job);cache.NavigationFailed(pawn,resource);cache.Ended(pawn,job,JobCondition.ErroredPather);
        Check(!cache.Allows(second,null,resource)&&cache.Allows(second,giver,blueprint),"取料失败只隔离材料");
        resource.Position=Cell(6,5);Check(cache.Allows(second,null,resource),"材料移动允许重新验证");
        cache.Started(pawn,job);job.targetB=other;job.targetC=other;pawn.carryTracker.CarriedThing=resource;
        cache.NavigationFailed(pawn,other);cache.Ended(pawn,job,JobCondition.ErroredPather);
        Check(!cache.Allows(second,giver,other)&&cache.Allows(second,giver,blueprint),"队列交付隔离当前需求");
        GenTicks.TicksGame=7000;cache.Prune();Check(cache.Allows(second,giver,other),"失败记录过期清理");
        Bill bill=new Bill();Job billJob=new Job {bill=bill,targetA=blueprint,workGiverDef=giver};
        cache.Ended(pawn,billJob,JobCondition.Errored);
        Check(!cache.AllowsBill(second,blueprint,bill)&&cache.AllowsBill(second,blueprint,new Bill()),"同一工作台的其他账单不受影响");
        Check(cache.Allows(second,giver,blueprint),"制作失败不应封禁整个工作台候选");
    }
}
