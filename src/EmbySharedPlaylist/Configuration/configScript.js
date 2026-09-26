/* global ApiClient, Dashboard */
define([], function () {
    'use strict';

    var PLUGIN_ID = '9ebe814e-9438-42b8-aa57-feea1ae92451';

    return function (view) {

        function load() {
            ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
                view.querySelector('#EnableSpikeEndpoints').checked = !!cfg.EnableSpikeEndpoints;
            });
        }

        function save() {
            ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (cfg) {
                cfg.EnableSpikeEndpoints = view.querySelector('#EnableSpikeEndpoints').checked;
                ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
        }

        view.addEventListener('viewshow', load);
        view.querySelector('#btnSave').addEventListener('click', save);
    };
});
