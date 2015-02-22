# SqlTools

An application for searching and browsing object scripts on Microsoft SQL Servers.

SqlTools can search by any or all of the following:

* Object Name
* Schema Name
* Object Definition - you can search *inside* the text of the objects' definitions

## Can you show me what it looks like?

Yes.

This is the interface for locating objects by searching on the SQL Server:

![Code View Interface](/Wiki/Images/SqlToolsSearchInterface.png)

Note that once you've queried the server, you can also filter/search within those results locally.

Clicking the "Script" button gets the SQL object definition for that object and then displays it to you in this interface:

![Code View Interface](/Wiki/Images/SqlToolsCodeViewInterface.png)

## Why do we need yet another way to view and script objects on MSSQL?

In my daily job I do a lot of prod support work on servers containing dozens databases, many of which have several thousand objects. I constantly need to find stored procs, views, etc. by name, but most
of the time I don't remember the FULL name of the object I'm looking for (so finding it in an alphabetical listing is very difficult). The search interface(s) in SQL Management Studio were always very cumbersome, so most of the time I was running queries like this over and over:

```
select *
from foo.sys.tables t
where t.name like '%something%'
```

...so I decided to make an application that would do this for me, with the added benefit of searching 
across all databases on a server (if desired) as well as also searching within the object scripts as well.

I have come across tools that do similar things, but they had problems like:

* They were very buggy
* They tried to do too much and didn't have enough focus on just *finding stuff*
* Were super-expensive

## Requirements

* .net 4.5 or higher 
  * ...although you could probably convert the project to 4.0 easily if you wanted to
* An MS SQL 2008 or higher database
  * Most of the testing and dev has been against SQL 2008 with some limited usage against 2012. So *it should be fine.* 
