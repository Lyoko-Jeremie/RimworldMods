using Verse;
using Jeremie.AtmosphereOverhaulPatch.Api;

namespace Jeremie.AtmosphereOverhaulPatch.Util
{
    /// <summary>
    /// 对 AO 持续源（RegisterContinuousSource / UpdateContinuousSource / UnregisterContinuousSource）
    /// 的生命周期封装。
    ///
    /// 用法：
    /// <code>
    /// object rates = AtmosphereApi.NewRates();
    /// AtmosphereApi.SetRate(rates, "TemperaturePerTick", 0.5f);
    /// SourceLease lease = SourceLease.Acquire(map, thing, "myKey", cell, rates);
    /// // 之后每 tick 改 rates 字段后 lease.Update()，建筑销毁时 lease.Dispose()
    /// </code>
    ///
    /// 所有失败都返回 false / null，不抛异常。
    /// </summary>
    public sealed class SourceLease : System.IDisposable
    {
        private readonly Map map;
        private readonly Thing owner;
        private readonly string key;
        private IntVec3 cell;
        private object handle;
        private object rates;
        private bool disposed;

        private SourceLease(Map map, Thing owner, string key, IntVec3 cell, object handle, object rates)
        {
            this.map = map;
            this.owner = owner;
            this.key = key;
            this.cell = cell;
            this.handle = handle;
            this.rates = rates;
        }

        public bool Valid
        {
            get
            {
                return !disposed
                       && handle != null
                       && map != null
                       && owner != null
                       && !owner.Destroyed;
            }
        }

        public IntVec3 Cell
        {
            get { return cell; }
        }

        public object Rates
        {
            get { return rates; }
        }

        /// <summary>
        /// 取得一个持续源：已有句柄就复用并更新，没有就注册。
        /// AO 不可用或参数无效时返回 null。
        /// </summary>
        public static SourceLease Acquire(Map map, Thing owner, string key, IntVec3 cell, object rates)
        {
            if (!AtmosphereApi.HasContinuousSources || map == null || owner == null || string.IsNullOrEmpty(key) || rates == null)
            {
                return null;
            }

            object handle = null;
            AtmosphereApi.TryGetContinuousSourceHandle(map, owner, key, out handle);

            if (handle == null)
            {
                handle = AtmosphereApi.RegisterContinuousSource(map, owner, key, cell, rates);
            }
            else
            {
                AtmosphereApi.UpdateContinuousSource(map, owner, handle, cell, rates);
            }

            if (handle == null)
            {
                return null;
            }

            return new SourceLease(map, owner, key, cell, handle, rates);
        }

        /// <summary>按当前 rates 内容刷新（格子不变）。</summary>
        public bool Update()
        {
            if (!Valid)
            {
                return false;
            }

            return AtmosphereApi.UpdateContinuousSource(map, owner, handle, cell, rates);
        }

        /// <summary>把源移动到新格子并刷新。</summary>
        public bool Update(IntVec3 newCell)
        {
            if (!Valid)
            {
                return false;
            }

            cell = newCell;
            return AtmosphereApi.UpdateContinuousSource(map, owner, handle, cell, rates);
        }

        /// <summary>替换 rates 对象并刷新。</summary>
        public bool Update(object newRates)
        {
            if (newRates == null)
            {
                return false;
            }

            rates = newRates;
            return Update();
        }

        /// <summary>把该源的所有速率清零（而不是注销）。</summary>
        public bool Zero()
        {
            if (!Valid)
            {
                return false;
            }

            object zeroed = AtmosphereApi.NewRates();
            if (zeroed == null)
            {
                return false;
            }

            return AtmosphereApi.UpdateContinuousSource(map, owner, handle, cell, zeroed);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                if (handle != null && map != null && owner != null)
                {
                    AtmosphereApi.UnregisterContinuousSource(map, owner, handle);
                }
            }
            catch
            {
                // 注销失败不应影响销毁流程
            }

            handle = null;
        }
    }
}
