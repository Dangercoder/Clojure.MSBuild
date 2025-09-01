(println "Testing Potion REPL with dependencies...")
(println "Attempting to import Npgsql.NpgsqlConnection...")

(try
  (import 'Npgsql.NpgsqlConnection)
  (println "✓ Successfully imported Npgsql.NpgsqlConnection")
  (println (str "  Class: " Npgsql.NpgsqlConnection))
  (catch Exception e
    (println (str "✗ Failed to import: " (.Message e)))))

(try
  (import 'Microsoft.Data.SqlClient.SqlConnection)
  (println "✓ Successfully imported Microsoft.Data.SqlClient.SqlConnection")
  (catch Exception e
    (println (str "✗ Failed to import: " (.Message e)))))

(println "\nTesting instantiation...")
(try
  (let [conn-str "Host=localhost;Database=test;Username=test;Password=test"]
    (println (str "  Creating NpgsqlConnection with: " conn-str))
    (let [conn (Npgsql.NpgsqlConnection. conn-str)]
      (println (str "  Connection created: " conn))
      (.Dispose conn)))
  (catch Exception e
    (println (str "✗ Failed to create connection: " (.Message e)))))

(println "\nAll tests complete!")