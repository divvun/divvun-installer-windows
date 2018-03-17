﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using Pahkat.Extensions;
using Pahkat.UI;
using Pahkat.UI.Main;
using Pahkat.UI.Shared;
using Castle.DynamicProxy.Generators.Emitters.SimpleAST;

namespace Pahkat.Service
{
    public interface IWindowConfig
    {
        Type WindowType { get; }
        Window Instance { get; }
    }
    
    public class WindowConfig : IWindowConfig
    {   
        public Type WindowType { get; }
        protected Window _instance;
        protected readonly Func<Window> _creator;

        public Window Instance => _instance ?? (_instance = _creator());
        
        public static IWindowConfig Create<T>() where T: Window, new()
        {
            return new WindowConfig(typeof(T), () => new T());
        }

        protected WindowConfig(Type type, Func<Window> creator)
        {
            WindowType = type;
            _creator = creator;
        }
    }

    public class CloseHandlingWindowConfig : IWindowConfig
    {
        public Type WindowType { get; }
        protected Window _instance;
        protected readonly Func<Window> _creator;
        
        public Window Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = _creator();
                    _instance.Closing += ClosingHandler;
                }

                return _instance;
            }
        }
        
        public static IWindowConfig Create<T>() where T: Window, new()
        {
            return new CloseHandlingWindowConfig(typeof(T), () => new T());
        }

        private CloseHandlingWindowConfig(Type type, Func<Window> creator)
        {
            WindowType = type;
            _creator = creator;
        }
        
        private void ClosingHandler(object sender, CancelEventArgs args)
        {
            args.Cancel = true;
            var w = _instance;
            w.Hide();
            w.Closing -= ClosingHandler;
            _instance = null;
        }
    }

    public interface IWindowService
    {
        void Show<T>() where T : Window;
        void Show<T>(WindowSaveState state) where T : Window;
        void Show<T>(IPageView pageView) where T : Window;
        void Show<T>(IPageView pageView, WindowSaveState? state) where T : Window;
        void Show<TWindow, TPage>()
            where TWindow : Window, IWindowPageView
            where TPage : IPageView, new();
        void Hide<T>() where T : Window;
        void Close<T>() where T : Window;
        IWindowConfig Get<T>() where T : Window;
    }
    
    public class WindowService : IWindowService
    {
        private readonly Dictionary<Type, IWindowConfig> _registry =
            new Dictionary<Type, IWindowConfig>();

        public static WindowService Create(params IWindowConfig[] windowConfigs)
        {
            return new WindowService(windowConfigs);
        }
        
        public WindowService(params IWindowConfig[] windowConfigs)
        {
            foreach (var cfg in windowConfigs)
            {
                if (_registry.ContainsKey(cfg.WindowType))
                {
                    throw new ArgumentException("Multiple configs with same type provided.");
                }
                _registry.Add(cfg.WindowType, cfg);
            }
        }
        
        public IWindowConfig Get<T>() where T: Window
        {
            var config = _registry.Get(typeof(T), null);
            if (config == null)
            {
                throw new ArgumentException("Cannot find type T in configuration registry.");
            }
            return config;
        }

        public void Show<T>() where T: Window
        {
            Get<T>().Instance.Show();
        }

        public void Show<T>(WindowSaveState state) where T : Window
        {
            var window = Get<T>().Instance;
            
            window.Left = state.Left;
            window.Top = state.Top;
            window.Height = state.Height;
            window.Width = state.Width;
            window.WindowState = state.WindowState;

            window.Show();
        }

        public void Show<T>(IPageView pageView, WindowSaveState? state) where T : Window
        {
            if (state != null)
            {
                Show<T>(state.Value);
            }

            Show<T>(pageView);
        }

        public void Show<T>(IPageView pageView) where T: Window
        {
            var x = (IWindowPageView) Get<T>().Instance;
            x.Show();
            x.ShowPage(pageView);
        }

        public void Show<TWindow, TPage>()
            where TWindow : Window, IWindowPageView
            where TPage : IPageView, new()
        {
            Show<TWindow>(new TPage());
        }

        public void Hide<T>() where T: Window
        {
            Get<T>().Instance.Hide();
        }

        public void Close<T>() where T : Window
        {
            Get<T>().Instance.Close();
        }
    }
}