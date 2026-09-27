/* global ApiClient, Dashboard */
define([], function () {
    'use strict';

    var PLUGIN_ID = '9ebe814e-9438-42b8-aa57-feea1ae92451';

    return function (view) {

        function load() {
            ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
                view.querySelector('#GracePasses').value = cfg.GracePasses != null ? cfg.GracePasses : 2;
                view.querySelector('#EnableDiagnostics').checked = cfg.EnableDiagnostics !== false;
                view.querySelector('#LogToConsole').checked = cfg.LogToConsole !== false;
                view.querySelector('#LogLevel').value = cfg.LogLevel || 'Info';
            });
        }

        function save() {
            ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
                var grace = parseInt(view.querySelector('#GracePasses').value, 10);
                cfg.GracePasses = isNaN(grace) || grace < 1 ? 1 : grace;
                cfg.EnableDiagnostics = view.querySelector('#EnableDiagnostics').checked;
                cfg.LogToConsole = view.querySelector('#LogToConsole').checked;
                cfg.LogLevel = view.querySelector('#LogLevel').value;
                ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
        }

        view.addEventListener('viewshow', load);
        view.querySelector('#btnSave').addEventListener('click', save);
    };
});
