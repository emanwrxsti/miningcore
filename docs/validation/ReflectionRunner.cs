using System;using System.Linq;using System.Reflection;using System.Threading.Tasks;
class Runner {
static async Task<int> Main(string[] args) {
 var asm=Assembly.LoadFrom(args[0]);int passed=0,failed=0,skipped=0;
 foreach(var type in asm.GetTypes().Where(x=>x.Namespace=="Miningcore.Tests.Blockchain.Quantus").OrderBy(x=>x.Name))
 foreach(var m in type.GetMethods(BindingFlags.Public|BindingFlags.Instance).OrderBy(x=>x.Name)) {
  var fact=m.GetCustomAttributes().FirstOrDefault(x=>x.GetType().Name.EndsWith("FactAttribute")||x.GetType().Name=="TheoryAttribute");if(fact==null)continue;
  var skip=fact.GetType().GetProperty("Skip")?.GetValue(fact) as string;if(skip!=null){Console.WriteLine("SKIP "+m.Name+": "+skip);skipped++;continue;}
  var data=m.GetCustomAttributesData().Where(x=>x.AttributeType.Name=="InlineDataAttribute").Select(x=>((System.Collections.Generic.IEnumerable<CustomAttributeTypedArgument>)x.ConstructorArguments[0].Value).Select(v=>v.Value).ToArray()).ToArray();
  if(data.Length==0)data=new[]{Array.Empty<object>()};
  foreach(var values in data) {object instance=null;try {instance=Activator.CreateInstance(type);
   for(int i=0;i<values.Length;i++)if(values[i]!=null)values[i]=Convert.ChangeType(values[i],m.GetParameters()[i].ParameterType);
   var result=m.Invoke(instance,values);if(result is Task task)await task;Console.WriteLine("PASS "+m.Name+"("+string.Join(",",values)+")");passed++;
  }catch(Exception e){Console.WriteLine("FAIL "+m.Name+": "+(e is TargetInvocationException?e.InnerException:e));failed++;}
  finally{if(instance is IAsyncDisposable ad)await ad.DisposeAsync();else if(instance is IDisposable d)d.Dispose();}
  }
 }
 Console.WriteLine($"RESULT: {passed} passed, {failed} failed, {skipped} skipped");return failed==0?0:1;
}}
