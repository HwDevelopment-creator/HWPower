# 📁 Views/ - Cartella per UserControl e MVVM (Future)

## 🎯 Scopo

Questa cartella è riservata ai **componenti UI riusabili** e alla **future migrazione a MVVM**.

## 📌 Cosa va qui

### UserControl (Componenti riusabili)
```
Views/
├── ProcessListControl.xaml           # UserControl per lista processi
├── ProcessListControl.xaml.cs
├── SystemMonitorControl.xaml         # UserControl per monitoring
├── SystemMonitorControl.xaml.cs
└── ...
```

### ViewModel (Future MVVM)
```
Views/
├── ViewModels/
│   ├── ProcessViewModel.cs           # VM per gestione processi
│   ├── SystemViewModel.cs            # VM per monitoring sistema
│   └── ...
```

## 🚀 Quando usare

**Adesso (Early):**
- ❌ Lascia vuota - il progetto funziona bene con MainWindow unico

**Futuro (Scala):**
- ✅ Estrarre sezioni del MainWindow in UserControl
- ✅ Aggiungere ViewModel per binding dati
- ✅ Testare ogni componente separatamente

## 📚 Esempio Futuro

```csharp
// Views/ProcessListControl.xaml.cs
public partial class ProcessListControl : UserControl
{
	private ProcessViewModel _viewModel;

	public ProcessListControl()
	{
		InitializeComponent();
		_viewModel = new ProcessViewModel();
		DataContext = _viewModel;
	}
}
```

## 💡 Vantaggi quando implementato

- ✅ Componenti riusabili
- ✅ Code-behind più piccoli
- ✅ MVVM pattern (testabile)
- ✅ Separazione UI/Logic

---

**Per ora questa cartella è un placeholder - non serve a niente!** 🎁
Ma quando il progetto cresca, saprai esattamente dove mettere i nuovi componenti.
