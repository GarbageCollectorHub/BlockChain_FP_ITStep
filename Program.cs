using BlockChain_FP_ITStep.Data;
using BlockChain_FP_ITStep.Hubs;
using BlockChain_FP_ITStep.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

/* ====  TODO / Ideas / Notes ====

- TODO: Main Index Page -> Mining Progress Bar  ( TODO: пока что не работает из за нового метода майнинга и отключения  SignalR), хочу сделать кастомный Лоадер и вывод attempts но мб сигналР не лучший вариант из за возможного ТаймАут у него, при долгой добычи блока. + После почистить JS в Index page
- TODO: BC Controller Index — refactor ViewBags into a single ViewModel

- В BlockChainService в методе AdjustDifficultyIfNeeded стоит искуственное ограниче на Difficulty для тестов (поле сервиса maxDifficultyTest). Потом можно убрать.
- На Index отображаются статические пары Private и Public Key, используемые для тестов... + там же Demo Setup btn
- Wallets пока нужно "зарегистрировать", чтобы они добавились в словарь Wallets сервиса. На странице Index отображаются для информативности и тестов.  ( + throw exception в MinePending() с меседжем если валлета там нету, и вывод меседжа в индекс через тоаст)

- В Стейкинге коины берутся из "воздуха" а надо выделять какоито пул монет которые будут распределятся на вознаграждение за стейкинг, и учитывать их в Circulating Supply
- Стейкинг возгнараждение должно учитыавтся в Circulating Supply

UI/UX - Tasks:
- Помічати у списку гаманців контрактні адреси (бейдж “contract” або тип).
- Додати простий перегляд конфігурації контракту (для TimeLock — показати UnlockBlockIndex).  (!например В SmartContract Controller - Details action)
- Показувати причину відхилення транзакції (toast/alert/TempData).

 ====================================== */




// DB context Factory
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration["ConnectionStrings:DefaultConnection"]);
});


// DI's
//builder.Services.AddScoped<BlockChainService>();

builder.Services.AddSingleton<BlockChainService>();   // Wallets мб всетаки надо в БД писать (хотя БЧ не хранит валлетс?),  пока что сделаем Синглтон чтобы коллеккция Валлетс сохранялась между запросами в UI для тестов + ДБ фактори.

// SignalR
builder.Services.AddSignalR();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=BlockChain}/{action=Index}/{id?}")
    .WithStaticAssets();

// signalR mining Hub
app.MapHub<MiningHub>("/miningHub");


//  DB scope
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}



app.Run();
