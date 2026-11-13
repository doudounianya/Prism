using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Database
{
    public interface IMemberRepository : IRepository<Member>
    {
        bool Login(string username, string password, out Member member);
    }
}
