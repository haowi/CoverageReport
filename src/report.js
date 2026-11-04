// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

// Filtering, sorting and collapsing of the file table on the summary page.
(function () {
  "use strict";
  var table = document.getElementById("files");
  if (!table) return;
  var filter = document.getElementById("filter");
  var groups = Array.prototype.slice.call(table.tBodies);

  function fileRows(group) {
    return Array.prototype.slice.call(group.rows, 1);
  }

  function apply() {
    var query = filter.value.trim().toLowerCase();
    groups.forEach(function (group) {
      var collapsed = group.rows[0].classList.contains("collapsed");
      var any = false;
      fileRows(group).forEach(function (row) {
        var match = row.getAttribute("data-path").toLowerCase().indexOf(query) !== -1;
        any = any || match;
        row.hidden = !match || (collapsed && !query);
      });
      group.hidden = !any;
    });
  }

  function setCollapsed(row, collapsed) {
    row.classList.toggle("collapsed", collapsed);
    row.querySelector(".toggle").setAttribute("aria-expanded", collapsed ? "false" : "true");
  }

  // "n/a" is stored as -1 and sorts after every value in either direction.
  function compare(a, b, column, direction) {
    var x = a.cells[column].getAttribute("data-v");
    var y = b.cells[column].getAttribute("data-v");
    if (column === 0) return direction * x.localeCompare(y);
    x = Number(x);
    y = Number(y);
    if (x === -1 || y === -1) return x === y ? 0 : x === -1 ? 1 : -1;
    return direction * (x - y);
  }

  function sort(th) {
    var column = Number(th.getAttribute("data-col"));
    var direction = th.getAttribute("aria-sort") === "ascending" ? -1 : 1;
    table.querySelectorAll("th[aria-sort]").forEach(function (other) {
      other.setAttribute("aria-sort", "none");
    });
    th.setAttribute("aria-sort", direction === 1 ? "ascending" : "descending");
    groups.sort(function (a, b) {
      return compare(a.rows[0], b.rows[0], column, direction);
    });
    groups.forEach(function (group) {
      table.appendChild(group);
      fileRows(group)
        .sort(function (a, b) {
          return compare(a, b, column, direction);
        })
        .forEach(function (row) {
          group.appendChild(row);
        });
    });
  }

  filter.addEventListener("input", apply);
  table.querySelectorAll("th[data-col] button").forEach(function (button) {
    button.addEventListener("click", function () {
      sort(button.parentNode);
    });
  });
  groups.forEach(function (group) {
    group.querySelector(".toggle").addEventListener("click", function () {
      setCollapsed(group.rows[0], !group.rows[0].classList.contains("collapsed"));
      apply();
    });
  });
  function setAll(collapsed) {
    groups.forEach(function (group) {
      setCollapsed(group.rows[0], collapsed);
    });
    apply();
  }
  document.getElementById("collapse").addEventListener("click", function () {
    setAll(true);
  });
  document.getElementById("expand").addEventListener("click", function () {
    setAll(false);
  });
})();
