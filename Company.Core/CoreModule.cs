using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prism.Ioc;
using Prism.Modularity;
using System.Text;
using System.Threading.Tasks;
using Company.Core.Extension;

namespace Company.Core
{
    public class CoreModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterAssembly(Assembly.GetExecutingAssembly());
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            containerProvider.InitializeAssembly(Assembly.GetExecutingAssembly());
        }
    }
}
