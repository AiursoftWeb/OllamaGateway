document.getElementById('providerSelect').addEventListener('change', function () {
            window.location.href = this.dataset.navigationPrefix + encodeURIComponent(this.value);
        });
