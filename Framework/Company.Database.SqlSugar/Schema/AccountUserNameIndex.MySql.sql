-- 可选维护脚本，仅用于 MySQL，应用不会自动运行。
-- 先备份 Member 表，并检查重名记录；有返回结果时应先人工核对，不能直接删除账户。
SELECT LOWER(UserName) AS UserNameKey, COUNT(*) AS DuplicateCount
FROM Member
GROUP BY LOWER(UserName)
HAVING COUNT(*) > 1;

-- 确认无重名且没有同等唯一索引后，才单独执行下面的语句。
-- 已存在唯一索引的数据库无需重复执行；不更改任何账户字段或密码。
CREATE UNIQUE INDEX UX_Member_UserName ON Member (UserName);
