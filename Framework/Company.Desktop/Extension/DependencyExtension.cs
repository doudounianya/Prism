using Company.Core.Ioc;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension
{
    /// <summary>
    /// 依赖注入扩展类，可以实现加载模块时，实例化标注为ExposedServiceAttriubute特性的类
    /// </summary>
    public static class DependencyExtension
    {
        private static List<Type> GetTypes(Assembly assembly)
        {
            var result = assembly.GetTypes().Where(t => t != null && t.IsClass && !t.IsAbstract &&
            t.CustomAttributes.Any(p => p.AttributeType == typeof(ExposedServiceAttribute))).ToList();

            return result;
        }


        /// <summary>
        /// 扩展IContainerRegistry接口的注册类型的功能
        /// </summary>
        /// <param name="container"></param>
        /// <param name="assembly"></param>
        public static void RegisterAssembly(this IContainerRegistry container, Assembly assembly)
        {
            var list = GetTypes(assembly);

            foreach (var type in list)
            {
                RegisterAssembly(container, type);
            }
        }


        private static IEnumerable<ExposedServiceAttribute> GetExposedServices(Type type)
        {
            var typeInfo = type.GetTypeInfo();
            return typeInfo.GetCustomAttributes<ExposedServiceAttribute>();
        }

        //public static void RegisterAssembly(IContainerRegistry container, Type type)
        //{
        //    var list = GetExposedServices(type).ToList();

        //    foreach (var item in list)
        //    {
        //        if (item.Lifetime == Lifetime.Singleton)
        //        {
        //            container.RegisterSingleton(type);//注册单例
        //        }

        //        foreach (var IType in item.Types)
        //        {
        //            if (item.Lifetime == Lifetime.Singleton)
        //            {
        //                container.RegisterSingleton(IType, type);//以接口注册单例
        //            }
        //            else if (item.Lifetime == Lifetime.Transient)
        //            {
        //                container.Register(IType, type);//以接口注册多例
        //            }
        //        }
        //    }
        //}

        public static void RegisterAssembly(IContainerRegistry container, Type type)
        {
            var attributes = GetExposedServices(type).ToList();
            if (!attributes.Any()) return;

            // 如果存在多个 ExposedServiceAttribute，检测是否冲突
            var lifetimes = attributes.Select(a => a.Lifetime).Distinct().ToList();
            if (lifetimes.Count > 1)
                throw new InvalidOperationException($"Multiple {nameof(ExposedServiceAttribute)} on {type} have conflicting lifetimes.");

            var lifetime = lifetimes.First();
            var interfaceTypes = attributes.SelectMany(a => a.Types).Distinct().ToList();

            // 先校验接口是否由该类型实现
            foreach (var iface in interfaceTypes)
            {
                if (!iface.IsAssignableFrom(type))
                    throw new InvalidOperationException($"{type} does not implement {iface}.");
            }

            if (lifetime == Lifetime.Singleton)
            {
                // 具体类型及接口共用一个注册，由容器统一创建、跟踪和释放一次。
                container.RegisterManySingleton(type, interfaceTypes.Prepend(type).Distinct().ToArray());
            }
            else // Transient
            {
                container.Register(type);
                foreach (var iface in interfaceTypes)
                {
                    container.Register(iface, type);
                }
            }
        }




        /// <summary>
        /// 初始化程序集中所有标注为ExposedServiceAttriubute特性的类，要求单例具自动加载AutoInitialize=true
        /// </summary>
        /// <param name="container"></param>
        /// <param name="assembly"></param>
        public static void InitializeAssembly(this IContainerProvider container, Assembly assembly)
        {
            var list = GetTypes(assembly);

            foreach (var item in list)
            {
                InitializeAssembly(container, item);
            }
        }

        private static void InitializeAssembly(IContainerProvider container, Type type)
        {
            var list = GetExposedServices(type);

            foreach (var item in list)
            {
                if (item.Lifetime == Lifetime.Singleton && item.AutoInitialize)
                {
                    container.Resolve(type);
                }
            }
        }
    }
}
