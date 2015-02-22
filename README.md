# SqlTools
An application for searching and browsing object scripts on Microsoft SQL Servers.

SqlTools can search by any or all of the following:

* Object Name
* Schema Name
* Object Script

## Can you show me what it looks like?

Yes.

![Code View Interface](/Wiki/Images/SqlToolsCodeViewInterface.png)

## Why do we need yet another way to view and script objects on MSSQL?

In my daily job I do a lot of prod support work on servers containing over a dozen databases and 
MANY thousands of objects. I constantly need to find stored procs, views, etc. by name, but most
of the time I don't remember the FULL name of the object I'm looking for. I always found searching for
objects by name in SQL Management Studio to be really cumbersome, so instead I found myself 
running a query like this over and over:

```
select *
from foo.sys.tables t
where t.name like '%something%'
```

So I decided to make an application that would do this for me, with the added benefit of searching 
across all databases on a server (if desired) as well as also searching within the object scripts as well.

## Requirements

* .net 4.5 or higher 
  * ...although you could probably convert the project to 4.0 easily if you wanted to
* An MS SQL 2008 or higher database
  * Most of the testing and dev has been against SQL 2008 with some limited usage against 2012. So *it should be fine.* 
