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

Clicking the "Script" button gets the SQL object definition for that object and then displays it to you in this interface:

![Code View Interface](/Wiki/Images/SqlToolsCodeViewInterface.png)

## Why do we need yet another way to view and script objects on MSSQL?

In my daily job I do a lot of prod support work on servers containing dozens of databases, 
many of which have several thousand objects. I constantly need to find stored procs, views, 
etc. by name, but most of the time I don't remember the FULL name of the object I'm looking 
for. To make matters worse, a lot of times I don't even remember which *database* it's in.

The search interface(s) in SQL Management Studio were always very cumbersome, so most of the time I was running queries
like this over and over:

```
select *
from foo.sys.tables t
where t.name like '%something%'
```

...so I decided to make an application that would do this for me, with the added benefit of searching 
across all databases on a server (if desired) as well as also searching within the objects' definitions as well.

I have come across tools that do similar things, but they had one or more of the following problems:

* They were very buggy
* They tried to do too much and didn't have enough focus on just *finding stuff*
* They were super-expensive

## Can you tell me other interesting facts about this thing?

Sure, I've got nothing better to do...

* Supports auth via Windows or SQL login with username/password
* The app's icon is a totally awesome "recliner" - it makes your life so much easier, you'll have more time to relax in your easy chair
* When searching for objects:
  * Allows you to filter/search objects by object name, schema name and/or object definition
  * Can search across individual DB's on a server or all of them
  * Searching databases doesn't generally cause too much impact on the server
  * Each database query is run asynchronously, so searching on a server with a large number of DB's will still happen quickly
  * After you search, you can use somewhat sophisticated filtering techniques to narrow down those results
* When viewing object definition scripts
  * Syntax highlighting, mainly thanks to [Avalon Edit](http://avalonedit.net/)
  * Will format object code using [Poor Man's T-SQL Formatter](http://architectshack.com/PoorMansTSqlFormatter.ashx)
  * Incremental find within object definition scripts.

## Requirements

* .net 4.5 or higher 
  * ...although you could probably convert the project to 4.0 easily if you wanted to
* An MS SQL 2008 or higher database
  * Most of the testing and dev has been against SQL 2008 with some limited usage against 2012. So *it should be fine.* 
