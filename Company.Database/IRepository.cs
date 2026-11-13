using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Database
{
    public interface IRepository<TEntity> where TEntity : EntityBase
    {
        bool Insert(TEntity entity);
        bool Update(TEntity entity);
        bool Delete(TEntity entity);
        List<TEntity> GetAll();
        TEntity GetEntity(int id);
    }
}
